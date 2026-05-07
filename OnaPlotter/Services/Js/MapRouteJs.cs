using Microsoft.JSInterop;

namespace OnaPlotter.Services.Js;

/// <summary>
/// <see cref="IMapRouteJs"/> backed by an <see cref="IJSObjectReference"/>
/// handle to the leafletInterop.js module. Centralises the
/// JSDisconnected / ObjectDisposed swallow that every call site
/// previously re-implemented. After <see cref="MarkDisposed"/> every
/// method becomes a silent no-op.
/// </summary>
public sealed class MapRouteJs : IMapRouteJs
{
    private readonly IJSObjectReference _module;
    private bool _disposed;

    public MapRouteJs(IJSObjectReference module)
    {
        _module = module ?? throw new ArgumentNullException(nameof(module));
    }

    public void MarkDisposed() => _disposed = true;

    public Task AddRouteAsync(string id, string? name, double[][] coords, double totalNm)
        => InvokeSafe("addRoute", id, name, coords, totalNm);

    public Task UpdateRouteAsync(string id, string? name, double[][] coords, double totalNm)
        => InvokeSafe("updateRoute", id, name, coords, totalNm);

    public Task RemoveRouteAsync(string id)
        => InvokeSafe("removeRoute", id);

    public Task SetActiveRouteAsync(double[][] coords, int wpIndex, string routeId, string routeName)
        => InvokeSafe("setActiveRoute", (object)coords, wpIndex, routeId, routeName);

    public Task ClearActiveRouteAsync()
        => InvokeSafe("clearActiveRoute");

    public Task SetActiveOverlayHiddenAsync(bool hidden)
        => InvokeSafe("setActiveOverlayHidden", hidden);

    public Task SetActiveRouteStoppingAsync(bool stopping)
        => InvokeSafe("setActiveRouteStopping", stopping);

    public Task SetActiveRouteTtgSecondsAsync(double? seconds)
        => InvokeSafe("setActiveRouteTtgSeconds", seconds);

    public Task ClearCourseLineAsync()
        => InvokeSafe("clearCourseLine");

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
