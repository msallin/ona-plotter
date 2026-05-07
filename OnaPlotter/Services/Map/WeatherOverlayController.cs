using OnaPlotter.Services.Js;
using OnaPlotter.Utilities;

namespace OnaPlotter.Services.Map;

/// <summary>
/// Owns the Layers-panel weather-overlay state: on/off + opacity. The
/// preview/commit split for opacity matters because a slider drag
/// fires up to ~20 events; persisting on each tick would hit
/// localStorage repeatedly. Preview pushes the live opacity to JS;
/// the commit on slider-release writes it through Settings.
///
/// Lifecycle: instantiated by Map.razor in <c>OnAfterRenderAsync</c>
/// once the JS module reference is available. Cleared via
/// <see cref="ToggleAsync"/>(false) on user toggle or via the JS
/// wrapper's MarkDisposed on page unload.
/// </summary>
public sealed class WeatherOverlayController
{
    /// <summary>RainViewer precipitation-radar tile template. Free,
    /// no API key, CORS-friendly. If we ever want wind / temperature
    /// overlays, OpenWeatherMap is the obvious upgrade - would need
    /// a per-user API key threaded through IAppSettings and a
    /// dropdown picker. Not worth the plumbing until someone asks.</summary>
    private const string RainViewerTileUrl =
        "https://tilecache.rainviewer.com/v2/radar/nowcast/256/{z}/{x}/{y}/2/1_1.png";

    private readonly IMapOverlaysJs _overlaysJs;
    private readonly IAppSettings _settings;
    private bool _visible;

    /// <summary>Whether the weather overlay is currently on the map.
    /// Bound by the Layers panel to render the toggle state.</summary>
    public bool Visible => _visible;

    public WeatherOverlayController(IMapOverlaysJs overlaysJs, IAppSettings settings)
    {
        _overlaysJs = overlaysJs ?? throw new ArgumentNullException(nameof(overlaysJs));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>Toggle the overlay on or off. Pushes the persisted
    /// opacity from <see cref="IAppSettings.WeatherOverlayOpacity"/>
    /// when enabling.</summary>
    public async Task ToggleAsync(bool enabled)
    {
        _visible = enabled;
        if (enabled)
        {
            await _overlaysJs.SetWeatherOverlayAsync(RainViewerTileUrl, _settings.WeatherOverlayOpacity);
        }
        else
        {
            await _overlaysJs.ClearWeatherOverlayAsync();
        }
    }

    /// <summary>
    /// Slider drag tick: push the new opacity to the live Leaflet
    /// layer so the helm sees the chart respond, but DON'T persist -
    /// writing localStorage on every drag tick fires ~20 disk writes
    /// plus as many StateHasChanged cascades for a one-second drag.
    /// Commit lands the final value via <see cref="CommitOpacityAsync"/>
    /// on slider release.
    /// </summary>
    public Task PreviewOpacityAsync(int percent)
    {
        if (!_visible) return Task.CompletedTask;
        return _overlaysJs.SetWeatherOverlayOpacityAsync(WeatherOpacity.PercentToFraction(percent));
    }

    /// <summary>Slider released: persist the final value via
    /// <see cref="IAppSettings"/> (round-trips to localStorage).</summary>
    public Task CommitOpacityAsync(int percent) =>
        _settings.SetWeatherOverlayOpacityAsync(WeatherOpacity.PercentToFraction(percent));
}
