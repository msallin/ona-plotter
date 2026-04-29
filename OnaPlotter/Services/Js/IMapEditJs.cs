namespace OnaPlotter.Services.Js;

/// <summary>
/// Typed C# wrapper over the leafletInterop.js route-edit, polygon-edit,
/// and measure-tool surface. Three sibling edit modes, each with its
/// own start / load / mutate / stop lifecycle, share one feature
/// wrapper because they all live on the same Map page partial and the
/// JSDisconnected / ObjectDisposed tolerance is identical across them.
/// </summary>
public interface IMapEditJs
{
    // ---- Route edit ---------------------------------------------------

    /// <summary>Enter route-edit mode (fresh route). Hooks click-to-add
    /// + drag-to-reposition handlers on the Leaflet map.</summary>
    Task StartRouteEditAsync();

    /// <summary>Exit route-edit mode. Removes the edit overlay + click
    /// handlers; safe to call when not in edit mode.</summary>
    Task StopRouteEditAsync();

    /// <summary>Seed the route-edit overlay with an existing route's
    /// vertices so the user can mutate in place. Coords are
    /// Leaflet-ordered <c>[lat, lon]</c> pairs.</summary>
    Task LoadRouteForEditAsync(double[][] coords);

    /// <summary>Remove the waypoint at the given 0-based index from
    /// the route-edit overlay.</summary>
    Task RemoveRouteEditWaypointAsync(int index);

    /// <summary>Flip the route-edit waypoint order in place.</summary>
    Task ReverseEditRouteAsync();

    // ---- Polygon edit -------------------------------------------------

    /// <summary>Enter polygon-edit mode (fresh region). Hooks
    /// click-to-add-vertex + drag-to-reposition handlers.</summary>
    Task StartPolygonEditAsync();

    /// <summary>Seed the polygon-edit overlay with an existing region's
    /// outer ring so the user can mutate vertices in place. Caller
    /// drops the closing duplicate vertex first.</summary>
    Task LoadPolygonForEditAsync(double[][] coords);

    /// <summary>Exit polygon-edit mode. Removes the edit overlay +
    /// click handlers.</summary>
    Task StopPolygonEditAsync();

    /// <summary>Drop the most-recently-added vertex from the
    /// polygon-edit overlay. Bound to the Undo button on the edit
    /// bar.</summary>
    Task UndoLastPolygonVertexAsync();

    /// <summary>Remove the vertex at the given 0-based index from the
    /// polygon-edit overlay.</summary>
    Task RemovePolygonEditVertexAsync(int index);

    // ---- Measure tool -------------------------------------------------

    /// <summary>Toggle the bearing-line measure tool. While active,
    /// taps drop ruler points and the map renders a multi-leg
    /// polyline with leg-distance labels.</summary>
    Task SetMeasureModeAsync(bool active);

    /// <summary>Drop a fresh two-point measurement: own boat as the
    /// moving anchor, the given lat/lon as the fixed endpoint.</summary>
    Task MeasureFromVesselToAsync(double lat, double lon);
}
