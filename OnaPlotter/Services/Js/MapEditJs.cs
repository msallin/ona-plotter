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
    private readonly SafeJsModule _js;

    public MapEditJs(IJSObjectReference module)
    {
        _js = new SafeJsModule(module);
    }

    public void MarkDisposed() => _js.MarkDisposed();

    /// <summary>Enter route-edit mode (fresh route). Hooks click-to-add
    /// + drag-to-reposition handlers on the Leaflet map.</summary>
    public Task StartRouteEditAsync()
        => _js.InvokeVoidAsync("startRouteEdit");

    /// <summary>Exit route-edit mode. Removes the edit overlay + click
    /// handlers; safe to call when not in edit mode.</summary>
    public Task StopRouteEditAsync()
        => _js.InvokeVoidAsync("stopRouteEdit");

    /// <summary>Seed the route-edit overlay with an existing route's
    /// vertices so the user can mutate in place. Coords are
    /// Leaflet-ordered <c>[lat, lon]</c> pairs.</summary>
    public Task LoadRouteForEditAsync(double[][] coords)
        => _js.InvokeVoidAsync("loadRouteForEdit", (object)coords);

    /// <summary>Remove the waypoint at the given 0-based index from
    /// the route-edit overlay.</summary>
    public Task RemoveRouteEditWaypointAsync(int index)
        => _js.InvokeVoidAsync("removeRouteEditWaypoint", index);

    /// <summary>Flip the route-edit waypoint order in place.</summary>
    public Task ReverseEditRouteAsync()
        => _js.InvokeVoidAsync("reverseEditRoute");

    /// <summary>Returns the current route-edit vertices as
    /// Leaflet-ordered <c>[lat, lon]</c> pairs. Returns null when the
    /// JS side has torn down. The C# poll derives stats (count +
    /// total NM) from this directly via
    /// <c>RouteProgress.TotalDistanceMeters</c> rather than a JS-side
    /// haversine loop, per the project rule (decisions in C#).</summary>
    public Task<double[][]?> GetEditRouteCoordsAsync()
        => _js.InvokeAsync<double[][]?>(null, "getEditRouteCoords");

    /// <summary>Enter polygon-edit mode (fresh region). Hooks
    /// click-to-add-vertex + drag-to-reposition handlers.</summary>
    public Task StartPolygonEditAsync()
        => _js.InvokeVoidAsync("startPolygonEdit");

    /// <summary>Seed the polygon-edit overlay with an existing region's
    /// outer ring so the user can mutate vertices in place. Caller
    /// drops the closing duplicate vertex first.</summary>
    public Task LoadPolygonForEditAsync(double[][] coords)
        => _js.InvokeVoidAsync("loadPolygonForEdit", (object)coords);

    /// <summary>Exit polygon-edit mode. Removes the edit overlay +
    /// click handlers.</summary>
    public Task StopPolygonEditAsync()
        => _js.InvokeVoidAsync("stopPolygonEdit");

    /// <summary>Drop the most-recently-added vertex from the
    /// polygon-edit overlay. Bound to the Undo button on the edit
    /// bar.</summary>
    public Task UndoLastPolygonVertexAsync()
        => _js.InvokeVoidAsync("undoLastPolygonVertex");

    /// <summary>Remove the vertex at the given 0-based index from the
    /// polygon-edit overlay.</summary>
    public Task RemovePolygonEditVertexAsync(int index)
        => _js.InvokeVoidAsync("removePolygonEditVertex", index);

    /// <summary>Returns the current polygon-edit vertices as
    /// Leaflet-ordered <c>[lat, lon]</c> pairs (open ring - no closing
    /// duplicate). Returns null when the JS side has torn down.</summary>
    public Task<double[][]?> GetPolygonEditCoordsAsync()
        => _js.InvokeAsync<double[][]?>(null, "getPolygonEditCoords");

    /// <summary>Toggle the bearing-line measure tool. While active,
    /// taps drop ruler points and the map renders a multi-leg
    /// polyline with leg-distance labels.</summary>
    public Task SetMeasureModeAsync(bool active)
        => _js.InvokeVoidAsync("setMeasureMode", active);

    /// <summary>Drop a fresh two-point measurement: own boat as the
    /// moving anchor, the given lat/lon as the fixed endpoint.</summary>
    public Task MeasureFromVesselToAsync(double lat, double lon)
        => _js.InvokeVoidAsync("measureFromVesselTo", lat, lon);

    /// <summary>Activate the measure tool and seed a single fixed
    /// point at the given lat/lon. The helm's next tap on the chart
    /// adds the second point and the running tooltip kicks in. Used
    /// by "Measure from here" in the map context menu.</summary>
    public Task MeasureFromPointAsync(double lat, double lon)
        => _js.InvokeVoidAsync("measureFromPoint", lat, lon);
}
