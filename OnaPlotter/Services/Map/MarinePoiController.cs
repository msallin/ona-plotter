using System.Collections.Immutable;
using OnaPlotter.Models;
using OnaPlotter.Services.Js;
using OnaPlotter.Services.Pois;
using OnaPlotter.Services.Settings;

namespace OnaPlotter.Services.Map;

/// <summary>
/// Owns the OSM marine-POI overlay's runtime: the cache-first render
/// path, the Overpass fetch trigger on viewport change, and the
/// per-category enable mask threaded from <see cref="IMarinePoiSettings"/>.
///
/// <para><b>Lifecycle</b>. Instantiated by Map.razor in
/// <c>OnAfterRenderAsync</c> once the JS module ref is up. Map.razor
/// calls <see cref="OnBoundsChangedAsync"/> from its existing
/// <c>OnMapBoundsChanged</c> JSInvokable and
/// <see cref="OnSettingsChangedAsync"/> from the
/// <see cref="IAppSettings.OnSettingsChanged"/> fan-out. <see cref="DisposeAsync"/>
/// runs on page tear-down.</para>
///
/// <para><b>Render model</b>. Cache is the source of truth for what's
/// drawn. On every viewport change:</para>
/// <list type="number">
///   <item><description>Render the cache-filtered union NOW (instant
///     paint of whatever we already know about the bbox).</description></item>
///   <item><description>If categories are enabled and the bbox is
///     plottable (zoom &gt;= MinZoom), kick a debounced background
///     fetch.</description></item>
///   <item><description>On fetch success, merge into the cache and
///     re-render.</description></item>
/// </list>
/// <para>Offline path: step 1 still works, step 2 returns empty per
/// the IMarinePoiService contract, the helm keeps seeing the cached
/// markers from prior visits.</para>
///
/// <para><b>Zoom gate</b>. Below <see cref="MinFetchZoom"/> the bbox
/// covers too much of the world for a useful Overpass call (a fetch
/// at z6 across a continent would either time out or saturate the
/// public endpoint). The cache still renders - we just skip new
/// fetches.</para>
/// </summary>
public sealed class MarinePoiController : IAsyncDisposable
{
    /// <summary>Below this zoom level we skip the Overpass fetch.
    /// z9 is roughly "regional view" - a few hundred km on a side -
    /// which is the smallest bbox that still returns a useful result
    /// without timing out. Cache renders work at any zoom.</summary>
    public const int MinFetchZoom = 9;

    /// <summary>Per-tick debounce on viewport change. Long enough to
    /// coalesce a scroll-pinch sequence into one fetch, short enough
    /// that a deliberate pan + wait shows fresh markers without the
    /// helm noticing. Matches the Photon search-box debounce idea
    /// (rapid keystrokes collapse into one HTTP).</summary>
    public static readonly TimeSpan FetchDebounce = TimeSpan.FromMilliseconds(800);

    private readonly IMarinePoiService _service;
    private readonly MarinePoiCache _cache;
    private readonly IMapMarinePoiJs _js;
    private readonly IMarinePoiSettings _settings;
    private readonly ILogger<MarinePoiController> _logger;

    // Last viewport seen. Updated on every OnBoundsChangedAsync; the
    // settings-change handler reads it to re-fetch with the same bbox
    // when the helm toggles a new category on.
    private double _west, _south, _east, _north;
    private double _zoom;
    private bool _hasViewport;

    // Fires on the next tick after a debounced viewport change.
    // Cancellation (helm panned again, page unmounting, settings
    // flipped) drops the in-flight delay before the HTTP call.
    private CancellationTokenSource? _fetchCts;

    public MarinePoiController(
        IMarinePoiService service,
        MarinePoiCache cache,
        IMapMarinePoiJs js,
        IMarinePoiSettings settings,
        ILogger<MarinePoiController> logger)
    {
        _service = service;
        _cache = cache;
        _js = js;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Master visible flag the JS layer carries. We always send the
    /// cache snapshot to JS regardless; the JS layer's visibility
    /// flag controls whether markers are added to the map.
    /// <para>Two ANDed gates: the helm's master "Show" flag (the
    /// section-header chip) and "at least one category enabled"
    /// (zero categories = nothing to show even if the master flag
    /// is on). Helm-tap on the master toggle hides every marker
    /// without losing the per-category selections.</para>
    /// </summary>
    public bool AnyCategoryEnabled =>
        _settings.MarinePoiOverlayVisible && GetEnabledCategories().Count > 0;

    /// <summary>
    /// Map page reports a new viewport. Cache-render immediately,
    /// then schedule a fetch.
    /// <para>Skips when the bbox is degenerate (NaN / infinity / zero
    /// area). Leaflet occasionally hands C# such a bbox during page
    /// reload, before the map container has finished laying out;
    /// forwarding it would fire a useless Overpass call against a
    /// 0-area bbox and stamp the controller's viewport state with a
    /// non-renderable region. The next real moveend re-arms the
    /// pipeline.</para>
    /// </summary>
    public async Task OnBoundsChangedAsync(
        double west, double south, double east, double north, double zoom)
    {
        if (!IsBboxRenderable(west, south, east, north, zoom)) return;
        _west = west;
        _south = south;
        _east = east;
        _north = north;
        _zoom = zoom;
        _hasViewport = true;
        await RenderFromCacheAsync();
        ScheduleFetch();
    }

    /// <summary>
    /// Returns true when the bbox + zoom combination is something we
    /// can render against. Catches:
    /// <list type="bullet">
    ///   <item><description>NaN / infinity from a half-initialised
    ///     map.</description></item>
    ///   <item><description>Out-of-range latitudes (Leaflet clamps to
    ///     ±85.05 for Web Mercator but JS can still pass garbage).</description></item>
    ///   <item><description>Zero-area bboxes from a pre-layout
    ///     <c>getBounds()</c>.</description></item>
    /// </list>
    /// Antimeridian-crossing viewports (<c>west &gt; east</c>) are
    /// allowed; the cache + Overpass treat them as-is, accepting the
    /// trailing-edge truncation.
    /// </summary>
    internal static bool IsBboxRenderable(double west, double south, double east, double north, double zoom)
    {
        if (!double.IsFinite(west) || !double.IsFinite(south)
            || !double.IsFinite(east) || !double.IsFinite(north)
            || !double.IsFinite(zoom)) return false;
        if (south < -90 || north > 90) return false;
        // Lat span must be non-zero. Use a strict-greater test so a
        // bounds report with south==north (degenerate) is rejected.
        if (north <= south) return false;
        // Lon span: handle the antimeridian case where west > east
        // (the bbox wraps around). The "degenerate" case is
        // west == east AND not crossing - reject that. Crossing is
        // valid (rare but real for Pacific transits).
        if (west == east) return false;
        return true;
    }

    /// <summary>
    /// Settings changed (helm toggled a category). Re-render the
    /// cache through the new mask, and fetch any newly-enabled
    /// categories. Called from <see cref="IAppSettings.OnSettingsChanged"/>.
    /// </summary>
    public async Task OnSettingsChangedAsync()
    {
        if (!_hasViewport) return;
        await _js.SetMarinePoisVisibleAsync(AnyCategoryEnabled);
        await RenderFromCacheAsync();
        ScheduleFetch();
    }

    /// <summary>
    /// Once-on-init: push the persisted visibility flag so the JS
    /// layer's master gate is in sync from first paint.
    /// </summary>
    public Task InitAsync() => _js.SetMarinePoisVisibleAsync(AnyCategoryEnabled);

    /// <summary>Cancel any in-flight debounce / HTTP and tear down.</summary>
    public ValueTask DisposeAsync()
    {
        _fetchCts?.Cancel();
        _fetchCts?.Dispose();
        _fetchCts = null;
        return ValueTask.CompletedTask;
    }

    private async Task RenderFromCacheAsync()
    {
        var enabled = GetEnabledCategories();
        if (enabled.Count == 0)
        {
            // Nothing to show - push an empty list so any leftover
            // markers from a previous category-set drop off.
            await _js.SetMarinePoisAsync([]);
            return;
        }
        var pois = await _cache.QueryAsync(_south, _west, _north, _east, enabled);
        await _js.SetMarinePoisAsync(ProjectForJs(pois));
    }

    private void ScheduleFetch()
    {
        var enabled = GetEnabledCategories();
        if (enabled.Count == 0) return;
        if (_zoom < MinFetchZoom) return;

        _fetchCts?.Cancel();
        _fetchCts = new CancellationTokenSource();
        var ct = _fetchCts.Token;

        // Capture bbox + categories at scheduling time. If the helm
        // pans / toggles again before the debounce fires, the new
        // ScheduleFetch call cancels this CTS and starts a fresh
        // one with the latest values.
        var south = _south;
        var west = _west;
        var north = _north;
        var east = _east;
        var capturedCategories = enabled;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(FetchDebounce, ct);
                if (ct.IsCancellationRequested) return;
                var fresh = await _service.FetchAsync(south, west, north, east, capturedCategories, ct);
                if (ct.IsCancellationRequested) return;
                if (fresh.Count > 0)
                {
                    await _cache.MergeAsync(fresh, ct);
                }
                if (ct.IsCancellationRequested) return;
                await RenderFromCacheAsync();
            }
            catch (OperationCanceledException)
            {
                // Helm panned / page unmounting / settings flipped -
                // the next ScheduleFetch will replace this one's work.
            }
            catch (Exception ex)
            {
                // Per IMarinePoiService contract the service swallows
                // its own transient errors; a leak here would be a
                // logic bug in the controller. Log so it's visible
                // and don't propagate to the page.
                _logger.LogWarning(ex, "[marine-poi] fetch task failed");
            }
        }, ct);
    }

    private ImmutableHashSet<MarinePoiCategory> GetEnabledCategories()
    {
        // Master visibility is the first gate so the cache-render +
        // scheduled-fetch paths drop everything when the helm taps
        // the section-header "Show" off, even if individual categories
        // are still ticked. Returning an empty set here cascades
        // through both call sites (RenderFromCacheAsync clears the
        // markers; ScheduleFetch returns early).
        if (!_settings.MarinePoiOverlayVisible)
            return ImmutableHashSet<MarinePoiCategory>.Empty;
        var b = ImmutableHashSet.CreateBuilder<MarinePoiCategory>();
        if (_settings.MarinePoiFuelEnabled) b.Add(MarinePoiCategory.Fuel);
        if (_settings.MarinePoiMarinaEnabled) b.Add(MarinePoiCategory.Marina);
        if (_settings.MarinePoiHarbourEnabled) b.Add(MarinePoiCategory.Harbour);
        if (_settings.MarinePoiMooringEnabled) b.Add(MarinePoiCategory.Mooring);
        if (_settings.MarinePoiSlipwayEnabled) b.Add(MarinePoiCategory.Slipway);
        if (_settings.MarinePoiPierEnabled) b.Add(MarinePoiCategory.Pier);
        if (_settings.MarinePoiChandleryEnabled) b.Add(MarinePoiCategory.Chandlery);
        if (_settings.MarinePoiDrinkingWaterEnabled) b.Add(MarinePoiCategory.DrinkingWater);
        if (_settings.MarinePoiPumpOutEnabled) b.Add(MarinePoiCategory.PumpOut);
        return b.ToImmutable();
    }

    /// <summary>
    /// Shape the JS layer expects: anonymous-typed array. Category is
    /// emitted as the enum NAME (string) so the JS switch can branch
    /// on a stable identifier instead of a numeric value the C# enum
    /// might re-order.
    /// </summary>
    private static object[] ProjectForJs(IReadOnlyList<MarinePoi> pois)
    {
        var arr = new object[pois.Count];
        for (int i = 0; i < pois.Count; i++)
        {
            var p = pois[i];
            arr[i] = new
            {
                id = p.Id,
                category = p.Category.ToString(),
                lat = p.Lat,
                lon = p.Lon,
                name = p.Name,
                tags = p.Tags,
            };
        }
        return arr;
    }
}
