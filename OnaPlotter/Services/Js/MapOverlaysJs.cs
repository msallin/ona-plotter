using Microsoft.JSInterop;

namespace OnaPlotter.Services.Js;

/// <summary>
/// <see cref="IMapOverlaysJs"/> backed by an
/// <see cref="IJSObjectReference"/> handle to the leafletInterop.js
/// module. Centralises the JSDisconnected / ObjectDisposed swallow
/// that every call site previously re-implemented. After
/// <see cref="MarkDisposed"/> every method becomes a silent no-op.
/// </summary>
public sealed class MapOverlaysJs : IMapOverlaysJs
{
    private readonly IJSObjectReference _module;
    private bool _disposed;

    public MapOverlaysJs(IJSObjectReference module)
    {
        _module = module ?? throw new ArgumentNullException(nameof(module));
    }

    public void MarkDisposed() => _disposed = true;

    public Task AddChartLayerAsync(string id, string tileUrl, int minZoom, int maxZoom, double opacity, double[]? bounds)
        => InvokeSafe("addChartLayer", id, tileUrl, minZoom, maxZoom, opacity, bounds);

    public Task RemoveChartLayerAsync(string id)
        => InvokeSafe("removeChartLayer", id);

    public Task SetChartLayerOrderAsync(string[] orderedIds)
        => InvokeSafe("setChartLayerOrder", orderedIds);

    public Task SetWeatherOverlayAsync(string tileUrl, double opacity)
        => InvokeSafe("setWeatherOverlay", tileUrl, opacity);

    public Task SetWeatherOverlayOpacityAsync(double opacity)
        => InvokeSafe("setWeatherOverlayOpacity", opacity);

    public Task ClearWeatherOverlayAsync()
        => InvokeSafe("clearWeatherOverlay");

    public Task SetColoredTrackAsync(double[][] points)
        => InvokeSafe("setColoredTrack", (object)points);

    public Task SetServerTrackAsync(double[][] coords, bool clipToBounds)
        => InvokeSafe("setServerTrack", (object)coords, clipToBounds);

    public Task SetServerTrackClipToBoundsAsync(bool enabled)
        => InvokeSafe("setServerTrackClipToBounds", enabled);

    public Task ClearServerTrackAsync()
        => InvokeSafe("clearServerTrack");

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
