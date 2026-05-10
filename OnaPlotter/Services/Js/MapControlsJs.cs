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

    public Task SetShipLinesVisibleAsync(bool enabled)
        => InvokeSafe("setShipLinesVisible", enabled);

    public Task SetRadarRangeRingsAsync(bool enabled, int count)
        => InvokeSafe("setRadarRangeRings", enabled, count);

    public Task SetNightModeAsync(bool enabled)
        => InvokeSafe("setNightMode", enabled);

    public Task SetGuardZoneAsync(double radiusNm, double lookaheadMin, double warningFactor)
        => InvokeSafe("setGuardZone", radiusNm, lookaheadMin, warningFactor);

    public Task SetCogVectorMinutesAsync(double ownMinutes, double aisMinutes)
        => InvokeSafe("setCogVectorMinutes", ownMinutes, aisMinutes);

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

    public Task SetRangeScaleHiddenAsync(bool hidden)
        => InvokeSafe("setRangeScaleHidden", hidden);

    private async Task InvokeSafe(string identifier, params object?[] args)
    {
        if (_disposed) return;
        try
        {
            await _module.InvokeVoidAsync(identifier, args);
        }
        catch (JSDisconnectedException) { /* page is unmounting */ }
        catch (ObjectDisposedException) { /* JS module disposed first */ }
        catch (JSException ex)
        {
            // A real JS-side regression. Log via Console.Error
            // (errorRelayBoot forwards to the SK server log) but DO
            // NOT propagate - the wrapper is called from
            // OnAfterRenderAsync + HandleSettingsChanged + per-tick
            // frame dispatch, and a propagated exception trips
            // Blazor's renderer error UI which empties the chart for
            // the rest of the session. The recent helm report:
            // "I enabled radar HUD ... I dont see infos on the chart
            // anymore" - root cause was a stale radar-overlay record
            // pointing at a removed Leaflet map; surfacing the JS
            // crash through here let it cascade past every subsequent
            // _controlsJs call. Log + swallow keeps the page alive
            // while the JS-side fix lands.
            Console.Error.WriteLine(
                $"[mapControls] JS '{identifier}' threw {ex.GetType().Name}: {ex.Message}");
        }
    }
}
