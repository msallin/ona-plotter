using OnaPlotter.Models;
using OnaPlotter.Services.Js;
using OnaPlotter.Services.Settings;

namespace OnaPlotter.Services.Map;

/// <summary>
/// Owns the chart-tile-layer lifecycle: enabled set, quick-bar
/// membership set, draw-order maintenance, and the JS interop pushes
/// for add/remove/reorder. Decouples the helm-driven decisions
/// (which charts are on, in what order, in the quick bar) from the
/// page's view-model concerns (caching the filtered list, paging
/// re-renders).
///
/// Lifecycle: instantiated by Map.razor in <c>OnAfterRenderAsync</c>
/// once the JS module reference is available. Disposal via the
/// wrapper's MarkDisposed.
///
/// Threading: Blazor WASM is single-threaded; the controller assumes
/// every public call runs on the renderer's synchronisation context.
/// </summary>
public sealed class ChartLayerController
{
    private readonly IMapOverlaysJs _overlaysJs;
    private readonly IChartSettings _settings;
    private readonly IMapDisplaySettings _display;
    private readonly Action<string, string> _toastError;       // (chart name, message)

    private readonly HashSet<string> _enabled = new(StringComparer.Ordinal);
    private readonly HashSet<string> _quickBar = new(StringComparer.Ordinal);

    /// <summary>Currently-enabled chart ids. Bound directly into
    /// LayersPanel (which expects a <see cref="HashSet{T}"/>) and the
    /// quick-bar render gate. Callers must NOT mutate from outside;
    /// the controller owns the lifecycle.</summary>
    public HashSet<string> EnabledCharts => _enabled;

    /// <summary>User-curated quick-bar set. Same binding rationale +
    /// caveat as <see cref="EnabledCharts"/>.</summary>
    public HashSet<string> QuickBarCharts => _quickBar;

    /// <summary>Optional hook invoked when the chart order or
    /// availability set changes. The page uses it to re-sort
    /// <c>availableCharts</c> via <see cref="ApplyOrder"/> and flush
    /// its filtered-layers cache.</summary>
    public Action? OnChartOrderChanged { get; set; }

    public ChartLayerController(
        IMapOverlaysJs overlaysJs,
        IChartSettings settings,
        IMapDisplaySettings display,
        Action<string, string> toastError)
    {
        _overlaysJs = overlaysJs ?? throw new ArgumentNullException(nameof(overlaysJs));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _display = display ?? throw new ArgumentNullException(nameof(display));
        _toastError = toastError ?? throw new ArgumentNullException(nameof(toastError));
    }

    /// <summary>Seed the local mirror sets from persisted settings
    /// (called once at page init). Avoids re-reading
    /// <see cref="IAppSettings.QuickBarChartIds"/> on every binding
    /// query.</summary>
    public void SeedFromSettings()
    {
        _quickBar.Clear();
        foreach (var id in _settings.QuickBarChartIds) _quickBar.Add(id);
    }

    /// <summary>
    /// Toggles a chart layer on or off, syncs the persisted enabled
    /// set, and (on enable) ensures the quick-bar membership +
    /// chart-order list contain the id.
    /// </summary>
    public async Task ToggleAsync(SignalkChart chart, bool enabled)
    {
        var tileUrl = chart.GetTileUrl();
        if (tileUrl is null) return;

        if (enabled)
        {
            try
            {
                // Upscale levels resolved here (master flag * configured
                // levels) so the JS side stays a thin renderer; the
                // decorator no-ops on 0. See OnaPlotter.Utilities.ChartUpscale.
                int upscale = OnaPlotter.Utilities.ChartUpscale.Effective(
                    _display.ChartUpscaleEnabled, _display.ChartUpscaleLevels);
                // chart.MaxZoom ?? 18 fallback: a chart with no metadata-
                // declared maxzoom (some legacy MBTiles, plus formats
                // that don't carry a pyramid descriptor) gets the
                // Leaflet default 18. With overzoom enabled, that
                // becomes the maxNativeZoom, and the helm gets a free
                // upscale window from 18 -> 18+levels. Intentional, and
                // the "blank tiles surface OSM underneath" honest
                // signal still applies if the chart actually tops out
                // earlier than 18.
                await _overlaysJs.AddChartLayerAsync(
                    chart.Identifier, tileUrl, chart.MinZoom ?? 1, chart.MaxZoom ?? 18,
                    0.8, chart.Bounds, upscale);
            }
            catch (Microsoft.JSInterop.JSException ex)
            {
                // A JS-side regression (wrong object shape, missing
                // layer type, etc.) must not bubble into Blazor's
                // error UI and freeze the render pass. Toast, log,
                // stay alive.
                _toastError(chart.Name, $"could not enable ({ex.Message})");
                return;
            }
            _enabled.Add(chart.Identifier);
            // Seed the chart order with any newly-seen id so user-
            // driven reorders have something to reorder. Append --
            // new charts go to the top of the draw stack (last
            // .addTo wins).
            if (!_settings.ChartOrder.Contains(chart.Identifier))
            {
                await _settings.SetChartOrderAsync(_settings.ChartOrder.Append(chart.Identifier));
                OnChartOrderChanged?.Invoke();
            }
            // Keep the quick-bar set in sync with any auto-enabled
            // chart (first-run OpenSeaMap seeding, future auto-rules).
            // Without this the chart would render but be missing from
            // the quick bar, so the user couldn't tap to hide it
            // again.
            if (_quickBar.Add(chart.Identifier))
                await _settings.SetQuickBarChartsAsync(_quickBar);
        }
        else
        {
            try
            {
                await _overlaysJs.RemoveChartLayerAsync(chart.Identifier);
            }
            catch (Microsoft.JSInterop.JSException ex)
            {
                _toastError(chart.Name, $"could not disable ({ex.Message})");
                return;
            }
            _enabled.Remove(chart.Identifier);
        }
        await _settings.SetEnabledChartsAsync(_enabled);
    }

    /// <summary>
    /// Layers-panel checkbox toggles membership of the quick-bar set
    /// (curation). Rendering is controlled by the quick bar itself.
    /// On untick we also tear down rendering so a removed chart
    /// doesn't linger as a ghost layer without a chip to toggle it
    /// off; on tick we activate the chart so the membership tap
    /// doubles as "I want this chart" and a separate quick-bar tap
    /// isn't required.
    /// </summary>
    public async Task ToggleQuickBarAsync(SignalkChart chart, bool inQuickBar)
    {
        if (inQuickBar)
        {
            _quickBar.Add(chart.Identifier);
            if (!_enabled.Contains(chart.Identifier))
                await ToggleAsync(chart, true);
        }
        else
        {
            _quickBar.Remove(chart.Identifier);
            if (_enabled.Contains(chart.Identifier))
                await ToggleAsync(chart, false);
        }
        await _settings.SetQuickBarChartsAsync(_quickBar);
    }

    /// <summary>
    /// Moves a chart up (-1) or down (+1) in the draw stack. "Up" in
    /// the UI means earlier in the displayed Charts list; the JS
    /// side re-applies z-index from the new order so the visual
    /// stack matches.
    /// </summary>
    public async Task ReorderAsync(SignalkChart chart, int direction)
    {
        var order = _settings.ChartOrder.ToList();

        // Defensive: ensure EVERY currently-enabled chart is
        // represented in the order list before we reorder. Without
        // this, a stale storage state (e.g. from the pre-fix
        // SetChartOrderAsync bug that wiped ChartOrder to just the
        // last-toggled chart) can leave a neighbour missing, so the
        // bounds check `swapWith >= order.Count` returns and the
        // user sees nothing happen.
        foreach (var enabledId in _enabled)
        {
            if (!order.Contains(enabledId)) order.Add(enabledId);
        }
        if (!order.Contains(chart.Identifier)) order.Add(chart.Identifier);

        int idx = order.IndexOf(chart.Identifier);
        // UI convention: up = visually up in the panel list, which is
        // earlier in the displayed Charts order. Since Charts is
        // iterated in ChartOrder (the same list we're editing),
        // "earlier" = lower index. direction is -1 for up / +1 for
        // down, so swapWith is simply idx + direction -- no flip.
        int swapWith = idx + direction;
        if (swapWith < 0 || swapWith >= order.Count) return;
        (order[idx], order[swapWith]) = (order[swapWith], order[idx]);

        await _settings.SetChartOrderAsync(order);
        OnChartOrderChanged?.Invoke();

        // Push the new draw order to JS so z-index on each layer
        // matches. JS regressions are toasted so the helm sees a
        // failure rather than a silently-wedged UI.
        try
        {
            await _overlaysJs.SetChartLayerOrderAsync(order.ToArray());
        }
        catch (Microsoft.JSInterop.JSException ex)
        {
            _toastError(chart.Name, $"reorder failed ({ex.Message})");
        }
    }

    /// <summary>
    /// Sorts the supplied chart list by
    /// <see cref="IAppSettings.ChartOrder"/>. Charts listed in the
    /// order come first (by index); unseen charts append in the
    /// supplied order. Pure -- the page assigns the result back to
    /// its own field. Called from <see cref="OnChartOrderChanged"/>
    /// hook.
    /// </summary>
    public List<SignalkChart> ApplyOrder(IEnumerable<SignalkChart> charts)
    {
        var order = _settings.ChartOrder;
        if (order.Count == 0) return charts.ToList();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < order.Count; i++) index[order[i]] = i;
        return charts
            .OrderBy(c => index.TryGetValue(c.Identifier, out var i) ? i : int.MaxValue)
            .ToList();
    }

    /// <summary>Mutable shorthand for the page's restore-on-init
    /// path. Adds a chart id to the local enabled mirror without
    /// firing the JS pipeline; used after <see cref="ToggleAsync"/>
    /// to keep the mirror in sync when an external caller restored
    /// the chart for us. (Currently unused; reserved for future
    /// init-snapshot batching.)</summary>
    internal void NoteEnabled(string id) => _enabled.Add(id);
}
