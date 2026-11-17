using Microsoft.JSInterop;
using OnaPlotter.Utilities;

namespace OnaPlotter.Services.Js;

/// <summary>
/// <see cref="IMapControlsJs"/> backed by an
/// <see cref="IJSObjectReference"/> handle to the leafletInterop.js
/// module. Centralises the JSDisconnected / ObjectDisposed swallow
/// so call sites don't each re-implement it. After
/// <see cref="MarkDisposed"/> every method becomes a silent no-op.
/// </summary>
public sealed class MapControlsJs : IMapControlsJs
{
    private readonly SafeJsModule _js;

    public MapControlsJs(IJSObjectReference module)
    {
        _js = new SafeJsModule(module, LogJsException);
    }

    public void MarkDisposed() => _js.MarkDisposed();

    public Task SetSignalKBaseUrlAsync(string url)
        => _js.InvokeVoidAsync("setSignalKBaseUrl", url);

    public Task SetMapOrientationAsync(string mode)
        => _js.InvokeVoidAsync("setMapOrientation", mode);

    public Task SetFollowAsync(bool follow)
        => _js.InvokeVoidAsync("setFollow", follow);

    public Task ClearLaylinesAsync()
        => _js.InvokeVoidAsync("clearLaylines");

    public Task SetShipLinesVisibleAsync(bool enabled)
        => _js.InvokeVoidAsync("setShipLinesVisible", enabled);

    public Task SetRadarRangeRingsAsync(bool enabled, int count)
        => _js.InvokeVoidAsync("setRadarRangeRings", enabled, count);

    public Task SetDistanceRingsAsync(bool enabled, double baseNm, int count)
        => _js.InvokeVoidAsync("setDistanceRings", enabled, baseNm, count);

    public Task SetRadarUseWireBearingAsync(bool value)
        => _js.InvokeVoidAsync("setRadarUseWireBearing", value);

    public Task SetRadarBearingCorrectionAsync(double degrees)
        => _js.InvokeVoidAsync("setRadarBearingCorrection", degrees);

    // The track palette rides along: night mode recolours the speed
    // tracks from SpeedColor's night ramp instead of filtering each path.
    public Task SetNightModeAsync(bool enabled)
        => _js.InvokeVoidAsync("setNightMode", enabled, SpeedColor.PaletteFor(enabled));

    public Task SetCogVectorMinutesAsync(double ownMinutes, double aisMinutes)
        => _js.InvokeVoidAsync("setCogVectorMinutes", ownMinutes, aisMinutes);

    public Task PanToAsync(double lat, double lon)
        => _js.InvokeVoidAsync("panTo", lat, lon);

    public Task ZoomToTrackAsync()
        => _js.InvokeVoidAsync("zoomToTrack");

    public Task FitBoundsAsync(double minLat, double minLon, double maxLat, double maxLon)
        => _js.InvokeVoidAsync("fitBounds", minLat, minLon, maxLat, maxLon);

    public Task EnableKeyboardShortcutsAsync<T>(DotNetObjectReference<T> dotNetRef, string[] keys) where T : class
        => _js.InvokeVoidAsync("enableKeyboardShortcuts", dotNetRef, keys);

    public Task DisableKeyboardShortcutsAsync()
        => _js.InvokeVoidAsync("disableKeyboardShortcuts");

    public Task ApplyFrameAsync(object frame)
        => _js.InvokeVoidAsync("applyFrame", frame);

    public Task ClearCurrentArrowAsync()
        => _js.InvokeVoidAsync("clearCurrentArrow");

    public async Task<double[]?> GetMapCenterAsync()
    {
        return await _js.InvokeAsync<double[]?>(null, "getMapCenter");
    }

    public Task SetRangeScaleHiddenAsync(bool hidden)
        => _js.InvokeVoidAsync("setRangeScaleHidden", hidden);

    // A real JS-side regression. Logged via Console.Error (errorRelayBoot
    // forwards it to the SK server log) and swallowed: this wrapper runs
    // from OnAfterRenderAsync, HandleSettingsChanged and the per-tick frame
    // dispatch, and a propagated exception trips Blazor's error UI, which
    // empties the chart for the rest of the session.
    private static void LogJsException(string identifier, JSException ex)
        => Console.Error.WriteLine(
            $"[mapControls] JS '{identifier}' threw {ex.GetType().Name}: {ex.Message}");
}
