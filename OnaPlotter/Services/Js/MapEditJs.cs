using Microsoft.JSInterop;

namespace OnaPlotter.Services.Js;

/// <summary>
/// <see cref="IMapEditJs"/> backed by an <see cref="IJSObjectReference"/>
/// handle to the leafletInterop.js module. Centralises the
/// JSDisconnected / ObjectDisposed swallow that every call site
/// previously re-implemented. After <see cref="MarkDisposed"/> every
/// method becomes a silent no-op.
/// </summary>
public sealed class MapEditJs : IMapEditJs
{
    private readonly IJSObjectReference _module;
    private bool _disposed;

    public MapEditJs(IJSObjectReference module)
    {
        _module = module ?? throw new ArgumentNullException(nameof(module));
    }

    public void MarkDisposed() => _disposed = true;

    public Task StartRouteEditAsync()
        => InvokeSafe("startRouteEdit");

    public Task StopRouteEditAsync()
        => InvokeSafe("stopRouteEdit");

    public Task LoadRouteForEditAsync(double[][] coords)
        => InvokeSafe("loadRouteForEdit", (object)coords);

    public Task RemoveRouteEditWaypointAsync(int index)
        => InvokeSafe("removeRouteEditWaypoint", index);

    public Task ReverseEditRouteAsync()
        => InvokeSafe("reverseEditRoute");

    public Task<double[]?> GetEditRouteStatsAsync()
        => InvokeReturning<double[]?>("getEditRouteStats");

    public Task<double[][]?> GetEditRouteCoordsAsync()
        => InvokeReturning<double[][]?>("getEditRouteCoords");

    public Task StartPolygonEditAsync()
        => InvokeSafe("startPolygonEdit");

    public Task LoadPolygonForEditAsync(double[][] coords)
        => InvokeSafe("loadPolygonForEdit", (object)coords);

    public Task StopPolygonEditAsync()
        => InvokeSafe("stopPolygonEdit");

    public Task UndoLastPolygonVertexAsync()
        => InvokeSafe("undoLastPolygonVertex");

    public Task RemovePolygonEditVertexAsync(int index)
        => InvokeSafe("removePolygonEditVertex", index);

    public Task<double[][]?> GetPolygonEditCoordsAsync()
        => InvokeReturning<double[][]?>("getPolygonEditCoords");

    public Task SetMeasureModeAsync(bool active)
        => InvokeSafe("setMeasureMode", active);

    public Task MeasureFromVesselToAsync(double lat, double lon)
        => InvokeSafe("measureFromVesselTo", lat, lon);

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

    /// <summary>Same swallow as <see cref="InvokeSafe"/> but for the
    /// non-void getEdit* / getPolygonEdit* calls. Returns the type's
    /// default (null for reference / nullable types) when the page is
    /// unmounting; the call sites skip their refresh path in that
    /// case.</summary>
    private async Task<T?> InvokeReturning<T>(string identifier, params object?[] args)
    {
        if (_disposed) return default;
        try
        {
            return await _module.InvokeAsync<T>(identifier, args);
        }
        catch (JSDisconnectedException) { return default; }
        catch (ObjectDisposedException) { return default; }
    }
}
