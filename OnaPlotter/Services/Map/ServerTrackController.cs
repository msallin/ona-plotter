using OnaPlotter.Services.Api;
using OnaPlotter.Services.Js;

namespace OnaPlotter.Services.Map;

/// <summary>
/// Owns the History-page-driven server-track polyline state: the
/// visibility flag, the helm-picked duration (1h..7d, "all" for the
/// long-term archive), and the "within current view" client-side
/// filter. Two interop paths:
/// <list type="bullet">
/// <item><description>Re-fetch + push coords when the helm enables
/// the layer or changes the duration.</description></item>
/// <item><description>Push the clip-to-bounds flag without
/// re-fetching when the helm toggles the viewport filter; the JS
/// module owns the cached coord array.</description></item>
/// </list>
///
/// Lifecycle: instantiated by Map.razor in <c>OnAfterRenderAsync</c>
/// once the JS module reference is available. Disposal is implicit
/// via the wrapper's MarkDisposed.
/// </summary>
public sealed class ServerTrackController
{
    private readonly IMapOverlaysJs _overlaysJs;
    private readonly ITrackApi _trackApi;
    private readonly Action<string> _emptyResultInfo;

    private bool _visible;
    // Helm-picked history window. Settings-seeded; the JS-side render
    // works the same whichever window applies.
    private string _duration;
    // Sampling resolution sent to the SK History API. Settings-seeded;
    // the History page uses the same ladder (1s..4h) so a helm who
    // picks "5m" on the History page sees the same sampling here.
    private string _resolution;
    private bool _withinBounds;

    /// <summary>Whether the server-track layer is currently on the
    /// map. Bound by the Layers panel.</summary>
    public bool Visible => _visible;

    /// <summary>Helm-picked history duration: <c>1h</c>, <c>6h</c>,
    /// <c>1d</c>, <c>3d</c>, <c>7d</c>, or <c>all</c> for the
    /// long-term archive.</summary>
    public string Duration => _duration;

    /// <summary>Helm-picked sampling resolution. Same ladder as the
    /// History page: <c>1s</c>, <c>30s</c>, <c>1m</c>, <c>5m</c>,
    /// <c>15m</c>, <c>30m</c>, <c>1h</c>, <c>4h</c>. A coarser
    /// resolution is the tool for taming a very long window
    /// (e.g. 7d / all) when the SK history endpoint times out.</summary>
    public string Resolution => _resolution;

    /// <summary>Whether the helm asked the JS layer to re-clip the
    /// cached coords to the current viewport (re-clips on pan/zoom
    /// without a re-fetch).</summary>
    public bool WithinBounds => _withinBounds;

    public ServerTrackController(
        IMapOverlaysJs overlaysJs,
        ITrackApi trackApi,
        Action<string> emptyResultInfo,
        string initialDuration = "1d",
        string initialResolution = "1m",
        bool initialWithinBounds = false)
    {
        _overlaysJs = overlaysJs ?? throw new ArgumentNullException(nameof(overlaysJs));
        _trackApi = trackApi ?? throw new ArgumentNullException(nameof(trackApi));
        _emptyResultInfo = emptyResultInfo ?? throw new ArgumentNullException(nameof(emptyResultInfo));
        _duration = initialDuration;
        _resolution = initialResolution;
        _withinBounds = initialWithinBounds;
    }

    /// <summary>Toggle the server-track layer. Enabling triggers a
    /// fetch + push for the current duration; disabling clears the
    /// JS-side polyline. Returns true when the resulting state
    /// reflects a successful fetch (enable path: server returned
    /// points) or a clean clear (disable path); false when the
    /// enable-path fetch returned nothing OR the underlying HTTP /
    /// parse failed (both surface as ClearServerTrackAsync today).
    /// The bool drives the Map page's consecutive-failure counter
    /// so the local in-memory trail can expand its render window
    /// during sustained server-history outages.</summary>
    public async Task<bool> ToggleAsync(bool enabled)
    {
        _visible = enabled;
        if (enabled)
        {
            return await ReloadAsync();
        }
        else
        {
            await _overlaysJs.ClearServerTrackAsync();
            return true;
        }
    }

    /// <summary>Helm changed the duration dropdown; re-fetch when the
    /// layer is currently visible. The bool result mirrors
    /// <see cref="ToggleAsync"/>'s semantics so a helm-initiated
    /// reload that yields no data also nudges the Map page's
    /// failure counter.</summary>
    public async Task<bool> SetDurationAsync(string duration)
    {
        if (string.Equals(duration, _duration, StringComparison.Ordinal)) return true;
        _duration = duration;
        if (_visible) return await ReloadAsync();
        return true;
    }

    /// <summary>Helm changed the resolution dropdown; re-fetch when
    /// the layer is currently visible. Same no-op-on-unchanged guard
    /// as <see cref="SetDurationAsync"/>: a re-render on an unrelated
    /// state change shouldn't fire a fresh request.</summary>
    public async Task<bool> SetResolutionAsync(string resolution)
    {
        if (string.Equals(resolution, _resolution, StringComparison.Ordinal)) return true;
        _resolution = resolution;
        if (_visible) return await ReloadAsync();
        return true;
    }

    /// <summary>
    /// Force a fresh fetch with the current duration / resolution.
    /// Used by the Map page's periodic refresher so the helm sees
    /// server-side history catching up to the live track without
    /// having to toggle the layer manually. Returns true when the
    /// fetch lands actual data; the Map page resets / increments
    /// its consecutive-failure counter from this signal and expands
    /// the local-trail render window when the server has been
    /// unhappy for several refreshes in a row. No-op (returns true)
    /// when the layer is hidden - a refresh on an invisible
    /// polyline would just burn an HTTP round-trip.
    /// </summary>
    public Task<bool> RefreshAsync() => _visible ? ReloadAsync() : Task.FromResult(true);

    /// <summary>Helm flipped the within-current-view toggle. No
    /// re-fetch: the JS module owns the cached coord array and
    /// re-clips on this toggle and on subsequent pan/zoom events.</summary>
    public Task SetWithinBoundsAsync(bool within)
    {
        _withinBounds = within;
        return _overlaysJs.SetServerTrackClipToBoundsAsync(within);
    }

    /// <summary>
    /// Fetches the server track for the current duration and pushes
    /// it to the JS layer as <c>[lat, lon, sogMs]</c> triples so the
    /// renderer can colour by speed bucket (same scheme as the
    /// local own-track polyline). The rich fetch carries SOG; nulls
    /// (e.g. early-cruise samples before SK derived SOG from the
    /// position delta) collapse to 0 m/s, which the JS speed bucket
    /// bins as the slowest band, visually consistent with "we did
    /// not see the boat moving fast" without dropping the segment.
    /// "all" maps to a 100-year ISO duration (P36500D) because the
    /// SignalK History API doesn't define an "everything" shape -
    /// a span larger than any plausible cruise is the pragmatic
    /// stand-in. The within-bounds flag rides along so the initial
    /// render respects the toggle if it was on before the duration
    /// change.
    /// </summary>
    private async Task<bool> ReloadAsync()
    {
        string apiSpan = _duration == "all" ? "P36500D" : _duration;
        // MapTrack path set: position + SOG only. The renderer below
        // colours by speed bucket; the rich-fetch shape carried COG /
        // heading / wind / depth that the Map overlay parses and then
        // throws away. Trimming the request keeps the server response
        // ~5 columns lighter per row on a busy harbour reload and
        // saves the per-row parse cost for fields we never look at.
        var points = await _trackApi.GetServerTrackPointsAsync(
            from: null, to: null, timespan: apiSpan, resolution: _resolution,
            pathSet: TrackFetchPathSet.MapTrack);
        if (points is not null && points.Length > 0)
        {
            // Project to [lat, lon, sogMs] triples for the JS speed-
            // colour renderer.
            var triples = new double[points.Length][];
            for (int i = 0; i < points.Length; i++)
            {
                triples[i] = [points[i].Latitude, points[i].Longitude,
                              points[i].SpeedOverGround ?? 0];
            }
            await _overlaysJs.SetServerTrackAsync(triples, _withinBounds);
            return true;
        }
        else
        {
            // Empty result OR transport failure (TrackApi returns null
            // on HttpRequestException / non-2xx / parse failure).
            // Leave _visible true so the helm's checkbox stays checked
            // - the previous behaviour flipped it off, which read as
            // "the app decided I didn't want this layer" and made the
            // helm re-check the box to try again. Clearing the JS
            // polyline + surfacing the info hint is enough; the next
            // duration / resolution change or RefreshAsync tick will
            // re-fetch automatically because _visible stayed true.
            await _overlaysJs.ClearServerTrackAsync();
            _emptyResultInfo("No history points returned for the selected duration.");
            return false;
        }
    }
}
