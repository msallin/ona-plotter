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
    private string _duration = "1d";
    // Sampling resolution sent to the SK History API. Default "1m"
    // matches the prior hardcoded value: a sensible balance between
    // detail and payload at a 1-day window. The History page uses the
    // same ladder (1s..4h); we pin the same set of values so a helm
    // who picks "5m" on the History page sees the same sampling here.
    private string _resolution = "1m";
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
        Action<string> emptyResultInfo)
    {
        _overlaysJs = overlaysJs ?? throw new ArgumentNullException(nameof(overlaysJs));
        _trackApi = trackApi ?? throw new ArgumentNullException(nameof(trackApi));
        _emptyResultInfo = emptyResultInfo ?? throw new ArgumentNullException(nameof(emptyResultInfo));
    }

    /// <summary>Toggle the server-track layer. Enabling triggers a
    /// fetch + push for the current duration; disabling clears the
    /// JS-side polyline.</summary>
    public async Task ToggleAsync(bool enabled)
    {
        _visible = enabled;
        if (enabled)
        {
            await ReloadAsync();
        }
        else
        {
            await _overlaysJs.ClearServerTrackAsync();
        }
    }

    /// <summary>Helm changed the duration dropdown; re-fetch when the
    /// layer is currently visible.</summary>
    public async Task SetDurationAsync(string duration)
    {
        if (string.Equals(duration, _duration, StringComparison.Ordinal)) return;
        _duration = duration;
        if (_visible) await ReloadAsync();
    }

    /// <summary>Helm changed the resolution dropdown; re-fetch when
    /// the layer is currently visible. Same no-op-on-unchanged guard
    /// as <see cref="SetDurationAsync"/>: a re-render on an unrelated
    /// state change shouldn't fire a fresh request.</summary>
    public async Task SetResolutionAsync(string resolution)
    {
        if (string.Equals(resolution, _resolution, StringComparison.Ordinal)) return;
        _resolution = resolution;
        if (_visible) await ReloadAsync();
    }

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
    /// it to the JS layer. "all" maps to a 100-year ISO duration
    /// (P36500D) because the SignalK History API doesn't define an
    /// "everything" shape -- a span larger than any plausible cruise
    /// is the pragmatic stand-in. The within-bounds flag rides along
    /// so the initial render respects the toggle if it was on before
    /// the duration change.
    /// </summary>
    private async Task ReloadAsync()
    {
        string apiSpan = _duration == "all" ? "P36500D" : _duration;
        var points = await _trackApi.GetServerTrackAsync(apiSpan, _resolution);
        if (points is not null && points.Length > 0)
        {
            await _overlaysJs.SetServerTrackAsync(points, _withinBounds);
        }
        else
        {
            _visible = false;
            await _overlaysJs.ClearServerTrackAsync();
            _emptyResultInfo("No history points returned for the selected duration.");
        }
    }
}
