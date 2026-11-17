using Microsoft.JSInterop;
using OnaPlotter.Utilities;

namespace OnaPlotter.Services.Js;

/// <summary>
/// <see cref="IMapOverlaysJs"/> backed by an
/// <see cref="IJSObjectReference"/> handle to the leafletInterop.js
/// module. Centralises the JSDisconnected / ObjectDisposed swallow
/// so call sites don't each re-implement it. After
/// <see cref="MarkDisposed"/> every method becomes a silent no-op.
/// </summary>
public sealed class MapOverlaysJs : IMapOverlaysJs
{
    private readonly SafeJsModule _js;

    public MapOverlaysJs(IJSObjectReference module)
    {
        _js = new SafeJsModule(module);
    }

    public void MarkDisposed() => _js.MarkDisposed();

    public Task AddChartLayerAsync(string id, string tileUrl, int minZoom, int maxZoom, double opacity, double[]? bounds, int upscaleLevels, string attribution, bool overlay)
        => _js.InvokeVoidAsync("addChartLayer", id, tileUrl, minZoom, maxZoom, opacity, bounds, upscaleLevels, attribution, overlay);

    public Task RemoveChartLayerAsync(string id)
        => _js.InvokeVoidAsync("removeChartLayer", id);

    public Task SetChartLayerOrderAsync(string[] orderedIds)
        => _js.InvokeVoidAsync("setChartLayerOrder", orderedIds);

    public Task SetChartFilterAsync(string cssFilter)
        => _js.InvokeVoidAsync("setChartFilter", cssFilter);

    public Task SetWeatherOverlayAsync(string tileUrl, double opacity)
        => _js.InvokeVoidAsync("setWeatherOverlay", tileUrl, opacity);

    public Task SetWeatherOverlayOpacityAsync(double opacity)
        => _js.InvokeVoidAsync("setWeatherOverlayOpacity", opacity);

    public Task ClearWeatherOverlayAsync()
        => _js.InvokeVoidAsync("clearWeatherOverlay");

    public Task SetColoredTrackAsync(TrackPolylineRun[] runs)
        => _js.InvokeVoidAsync("setColoredTrack", (object)runs);

    public Task SetServerTrackAsync(double[][] coords, bool clipToBounds)
        => _js.InvokeVoidAsync("setServerTrack", (object)coords, clipToBounds);

    public Task SetServerTrackClipToBoundsAsync(bool enabled)
        => _js.InvokeVoidAsync("setServerTrackClipToBounds", enabled);

    public Task ClearServerTrackAsync()
        => _js.InvokeVoidAsync("clearServerTrack");
}
