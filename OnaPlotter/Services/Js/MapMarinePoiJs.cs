using Microsoft.JSInterop;

namespace OnaPlotter.Services.Js;

/// <summary>
/// <see cref="IMapMarinePoiJs"/> backed by an
/// <see cref="IJSObjectReference"/> handle to the leafletInterop.js
/// module. Centralises the JSDisconnected / ObjectDisposed swallow
/// matching <see cref="MapAisJs"/>.
/// </summary>
public sealed class MapMarinePoiJs : IMapMarinePoiJs
{
    private readonly SafeJsModule _js;

    public MapMarinePoiJs(IJSObjectReference module)
    {
        _js = new SafeJsModule(module);
    }

    /// <summary>Mark the wrapper as disposed so subsequent calls
    /// silently no-op. Called by Map.razor's <c>DisposeAsync</c> path
    /// before it disposes the module reference.</summary>
    public void MarkDisposed() => _js.MarkDisposed();

    public Task SetMarinePoisAsync(object[] pois)
        => _js.InvokeVoidAsync("setMarinePois", (object)pois);

    public Task SetMarinePoisVisibleAsync(bool visible)
        => _js.InvokeVoidAsync("setMarinePoisVisible", visible);
}
