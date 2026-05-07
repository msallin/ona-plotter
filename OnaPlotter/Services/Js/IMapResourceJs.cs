namespace OnaPlotter.Services.Js;

/// <summary>
/// Typed C# wrapper over the leafletInterop.js user-placed-resource
/// surface: waypoints, notes, regions, and the circle preview used by
/// the new-region dialog. These all share the "draw / undraw a single
/// marker by id" lifecycle plus a few "clear all" + "focus" helpers,
/// so they live on the same wrapper rather than fragmenting into three
/// near-identical contracts.
/// </summary>
public interface IMapResourceJs
{
    // ---- Waypoints ----------------------------------------------------

    /// <summary>Draw a waypoint pin at the given lat/lon. Nullable
    /// coords mirror the call sites that pass <see cref="double?"/>
    /// straight through after a non-null gate. <paramref name="createdAtIso"/>
    /// renders in the popup as the "Created" line; null = dash
    /// (Freeboard / KIP / pre-feature OnaPlotter waypoints don't
    /// carry the field).</summary>
    Task AddWaypointMarkerAsync(string id, double? lat, double? lon, string? name, string? createdAtIso);

    /// <summary>Remove a previously-drawn waypoint pin.</summary>
    Task RemoveWaypointMarkerAsync(string id);

    // ---- Notes --------------------------------------------------------

    /// <summary>Draw a note marker at the given lat/lon. Position type
    /// is non-nullable double on the model, matching the JS shape.
    /// <paramref name="createdAtIso"/> renders in the popup as the
    /// "Created" line; null = dash (notes from Freeboard / KIP or
    /// pre-feature OnaPlotter notes don't carry the field).</summary>
    Task AddNoteMarkerAsync(string id, double lat, double lon, string? title, string? description, string? createdAtIso);

    /// <summary>Remove a previously-drawn note marker.</summary>
    Task RemoveNoteMarkerAsync(string id);

    /// <summary>Drop every note marker currently on the map. Used when
    /// the helm hides notes via the Layers panel.</summary>
    Task ClearNotesAsync();

    /// <summary>Open the popup for the note with the given id (deep-link
    /// from the Notes panel's Focus button).</summary>
    Task OpenNotePopupAsync(string id);

    // ---- Regions ------------------------------------------------------

    /// <summary>Draw a region polygon. Outer rings are stored as
    /// <see cref="IReadOnlyList{T}"/> on <c>SignalkRegion</c>; pass
    /// the same shape through. The popup carries the helm-facing
    /// metadata: <paramref name="isHazard"/> (drives both the red
    /// stroke and the warning glyph at the centroid),
    /// <paramref name="areaSqM"/> (computed by C# via
    /// PolygonGeometry so the unit-test catches a regression),
    /// <paramref name="centerLat"/> / <paramref name="centerLon"/>
    /// + <paramref name="radiusMeters"/> (set when the region was
    /// created as a circle so the popup renders "centre + radius"
    /// instead of a vertex dump), and
    /// <paramref name="createdAtIso"/> (ISO-8601 UTC; null = dash).
    /// </summary>
    Task AddRegionAsync(string id, IReadOnlyList<double[][]> rings,
        string? title, string? description, bool isHazard,
        double areaSqM,
        double? centerLat, double? centerLon, double? radiusMeters,
        string? createdAtIso);

    /// <summary>Remove a previously-drawn region polygon.</summary>
    Task RemoveRegionAsync(string id);

    /// <summary>Drop every region polygon currently on the map.</summary>
    Task ClearRegionsAsync();

    /// <summary>Pan + fit-bounds onto the named region's first ring.
    /// Used by the Resources-panel Focus button.</summary>
    Task FocusRegionAsync(string id, double[][] firstRing);

    // ---- Circle preview ----------------------------------------------

    /// <summary>Draw / move the circle-preview ring used by the
    /// new-region dialog while the helm is dialling in the radius.</summary>
    Task SetCirclePreviewAsync(double lat, double lon, double radiusMeters);

    /// <summary>Drop the circle-preview ring (Cancel / Save /
    /// mode-switch).</summary>
    Task ClearCirclePreviewAsync();
}
