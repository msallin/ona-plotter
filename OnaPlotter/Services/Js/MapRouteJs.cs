using Microsoft.JSInterop;

namespace OnaPlotter.Services.Js;

/// <summary>
/// <see cref="IMapRouteJs"/> backed by an <see cref="IJSObjectReference"/>
/// handle to the leafletInterop.js module. Centralises the
/// JSDisconnected / ObjectDisposed swallow so call sites don't each
/// re-implement it. After <see cref="MarkDisposed"/> every
/// method becomes a silent no-op.
/// </summary>
public sealed class MapRouteJs : IMapRouteJs
{
    private readonly SafeJsModule _js;

    public MapRouteJs(IJSObjectReference module)
    {
        _js = new SafeJsModule(module);
    }

    public void MarkDisposed() => _js.MarkDisposed();

    public Task AddRouteAsync(string id, string? name, double[][] coords, double totalNm)
        => _js.InvokeVoidAsync("addRoute", id, name, coords, totalNm);

    public Task UpdateRouteAsync(string id, string? name, double[][] coords, double totalNm)
        => _js.InvokeVoidAsync("updateRoute", id, name, coords, totalNm);

    public Task RemoveRouteAsync(string id)
        => _js.InvokeVoidAsync("removeRoute", id);

    public Task SetActiveRouteAsync(double[][] coords, int wpIndex, string routeId, string routeName, double totalNm)
        => _js.InvokeVoidAsync("setActiveRoute", (object)coords, wpIndex, routeId, routeName, totalNm);

    public Task ClearActiveRouteAsync()
        => _js.InvokeVoidAsync("clearActiveRoute");

    public Task SetActiveOverlayHiddenAsync(bool hidden)
        => _js.InvokeVoidAsync("setActiveOverlayHidden", hidden);

    public Task SetActiveRouteStoppingAsync(bool stopping)
        => _js.InvokeVoidAsync("setActiveRouteStopping", stopping);

    public Task ClearCourseLineAsync()
        => _js.InvokeVoidAsync("clearCourseLine");
}
