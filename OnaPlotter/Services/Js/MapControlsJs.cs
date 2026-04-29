using Microsoft.JSInterop;

namespace OnaPlotter.Services.Js;

/// <summary>
/// <see cref="IMapControlsJs"/> backed by an
/// <see cref="IJSObjectReference"/> handle to the leafletInterop.js
/// module. Centralises the JSDisconnected / ObjectDisposed swallow
/// that every call site previously re-implemented. After
/// <see cref="MarkDisposed"/> every method becomes a silent no-op.
/// </summary>
public sealed class MapControlsJs : IMapControlsJs
{
    private readonly IJSObjectReference _module;
    private bool _disposed;

    public MapControlsJs(IJSObjectReference module)
    {
        _module = module ?? throw new ArgumentNullException(nameof(module));
    }

    public void MarkDisposed() => _disposed = true;

    public Task SetSignalKBaseUrlAsync(string url)
        => InvokeSafe("setSignalKBaseUrl", url);

    public Task SetMapOrientationAsync(string mode)
        => InvokeSafe("setMapOrientation", mode);

    public Task SetFollowAsync(bool follow)
        => InvokeSafe("setFollow", follow);

    public Task ClearLaylinesAsync()
        => InvokeSafe("clearLaylines");

    public Task SetNightModeAsync(bool enabled)
        => InvokeSafe("setNightMode", enabled);

    public Task SetGuardZoneAsync(double radiusNm, double lookaheadMin, double warningFactor)
        => InvokeSafe("setGuardZone", radiusNm, lookaheadMin, warningFactor);

    public Task PanToAsync(double lat, double lon)
        => InvokeSafe("panTo", lat, lon);

    public Task ZoomToTrackAsync()
        => InvokeSafe("zoomToTrack");

    public Task FitBoundsAsync(double minLat, double minLon, double maxLat, double maxLon)
        => InvokeSafe("fitBounds", minLat, minLon, maxLat, maxLon);

    public Task EnableKeyboardShortcutsAsync<T>(DotNetObjectReference<T> dotNetRef) where T : class
        => InvokeSafe("enableKeyboardShortcuts", dotNetRef);

    public Task DisableKeyboardShortcutsAsync()
        => InvokeSafe("disableKeyboardShortcuts");

    public Task ApplyFrameAsync(object frame)
        => InvokeSafe("applyFrame", frame);

    public Task SetMobAsync(double lat, double lon)
        => InvokeSafe("setMob", lat, lon);

    public Task ClearMobAsync()
        => InvokeSafe("clearMob");

    public Task ClearCurrentArrowAsync()
        => InvokeSafe("clearCurrentArrow");

    public async Task<double[]?> GetMapCenterAsync()
    {
        if (_disposed) return null;
        try
        {
            return await _module.InvokeAsync<double[]>("getMapCenter");
        }
        catch (JSDisconnectedException) { return null; }
        catch (ObjectDisposedException) { return null; }
    }

    private async Task InvokeSafe(string identifier, params object?[] args)
    {
        if (_disposed) return;
        try
        {
            await _module.InvokeVoidAsync(identifier, args);
        }
        catch (JSDisconnectedException) { /* page is unmounting */ }
        catch (ObjectDisposedException) { /* JS module disposed first */ }
    }
}
