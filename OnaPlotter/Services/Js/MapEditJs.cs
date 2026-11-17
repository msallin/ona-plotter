using Microsoft.JSInterop;

namespace OnaPlotter.Services.Js;

/// <summary>
/// Typed wrapper over the leafletInterop.js route-edit, polygon-edit and
/// measure-tool surface, backed by an <see cref="IJSObjectReference"/>
/// handle to the leafletInterop.js module. Centralises the
/// JSDisconnected / ObjectDisposed swallow so call sites don't each
/// re-implement it. After <see cref="MarkDisposed"/> every
/// method becomes a silent no-op.
/// </summary>
public sealed class MapEditJs
{
    private readonly IJSObjectReference _module;
    private bool _disposed;

    public MapEditJs(IJSObjectReference module)
    {
        _module = module ?? throw new ArgumentNullException(nameof(module));
    }

    public void MarkDisposed() => _disposed = true;

    /// <summary>Enter route-edit mode (fresh route). Hooks click-to-add
    /// + drag-to-reposition handlers on the Leaflet map.</summary>
    public Task StartRouteEditAsync()
        => InvokeSafe("startRouteEdit");

    /// <summary>Exit route-edit mode. Removes the edit overlay + click
    /// handlers; safe to call when not in edit mode.</summary>
    public Task StopRouteEditAsync()
        => InvokeSafe("stopRouteEdit");

    /// <summary>Seed the route-edit overlay with an existing route's
    /// vertices so the user can mutate in place. Coords are
    /// Leaflet-ordered <c>[lat, lon]</c> pairs.</summary>
    public Task LoadRouteForEditAsync(double[][] coords)
        => InvokeSafe("loadRouteForEdit", (object)coords);

    /// <summary>Remove the waypoint at the given 0-based index from
    /// the route-edit overlay.</summary>
    public Task RemoveRouteEditWaypointAsync(int index)
        => InvokeSafe("removeRouteEditWaypoint", index);

    /// <summary>Flip the route-edit waypoint order in place.</summary>
    public Task ReverseEditRouteAsync()
        => InvokeSafe("reverseEditRoute");

    /// <summary>Returns the current route-edit vertices as
    /// Leaflet-ordered <c>[lat, lon]</c> pairs. Returns null when the
    /// JS side has torn down. The C# poll derives stats (count +
    /// total NM) from this directly via
    /// <c>RouteProgress.TotalDistanceMeters</c> rather than a JS-side
    /// haversine loop, per the project rule (decisions in C#).</summary>
    public Task<double[][]?> GetEditRouteCoordsAsync()
        => InvokeReturning<double[][]?>("getEditRouteCoords");

    /// <summary>Enter polygon-edit mode (fresh region). Hooks
    /// click-to-add-vertex + drag-to-reposition handlers.</summary>
    public Task StartPolygonEditAsync()
        => InvokeSafe("startPolygonEdit");

    /// <summary>Seed the polygon-edit overlay with an existing region's
    /// outer ring so the user can mutate vertices in place. Caller
    /// drops the closing duplicate vertex first.</summary>
    public Task LoadPolygonForEditAsync(double[][] coords)
        => InvokeSafe("loadPolygonForEdit", (object)coords);

    /// <summary>Exit polygon-edit mode. Removes the edit overlay +
    /// click handlers.</summary>
    public Task StopPolygonEditAsync()
        => InvokeSafe("stopPolygonEdit");

    /// <summary>Drop the most-recently-added vertex from the
    /// polygon-edit overlay. Bound to the Undo button on the edit
    /// bar.</summary>
    public Task UndoLastPolygonVertexAsync()
        => InvokeSafe("undoLastPolygonVertex");

    /// <summary>Remove the vertex at the given 0-based index from the
    /// polygon-edit overlay.</summary>
    public Task RemovePolygonEditVertexAsync(int index)
        => InvokeSafe("removePolygonEditVertex", index);

    /// <summary>Returns the current polygon-edit vertices as
    /// Leaflet-ordered <c>[lat, lon]</c> pairs (open ring - no closing
    /// duplicate). Returns null when the JS side has torn down.</summary>
    public Task<double[][]?> GetPolygonEditCoordsAsync()
        => InvokeReturning<double[][]?>("getPolygonEditCoords");

    /// <summary>Toggle the bearing-line measure tool. While active,
    /// taps drop ruler points and the map renders a multi-leg
    /// polyline with leg-distance labels.</summary>
    public Task SetMeasureModeAsync(bool active)
        => InvokeSafe("setMeasureMode", active);

    /// <summary>Drop a fresh two-point measurement: own boat as the
    /// moving anchor, the given lat/lon as the fixed endpoint.</summary>
    public Task MeasureFromVesselToAsync(double lat, double lon)
        => InvokeSafe("measureFromVesselTo", lat, lon);

    /// <summary>Activate the measure tool and seed a single fixed
    /// point at the given lat/lon. The helm's next tap on the chart
    /// adds the second point and the running tooltip kicks in. Used
    /// by "Measure from here" in the map context menu.</summary>
    public Task MeasureFromPointAsync(double lat, double lon)
        => InvokeSafe("measureFromPoint", lat, lon);

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
