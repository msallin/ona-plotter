using OnaPlotter.Models;
using OnaPlotter.Services.Js;

namespace OnaPlotter.Services.Map;

/// <summary>
/// Owns the batch and toggle-visibility lifecycle for the user-placed
/// resources rendered as map markers: waypoints, notes, and regions.
/// The CRUD flows themselves stay on Map.razor.cs (they need the
/// dialog state which belongs to the page); this controller is the
/// "wire a list to the map and let the helm hide/show it" facade.
///
/// Lifecycle: instantiated by Map.razor in <c>OnAfterRenderAsync</c>
/// once the JS module reference is available. Disposal via the
/// wrapper's MarkDisposed.
/// </summary>
public sealed class MapResourceController
{
    private readonly IMapResourceJs _resourceJs;
    private bool _notesVisible = true;
    private bool _regionsVisible = true;

    /// <summary>Whether the notes layer is currently shown. Bound by
    /// the Layers panel checkbox.</summary>
    public bool NotesVisible => _notesVisible;

    /// <summary>Whether the regions layer is currently shown.</summary>
    public bool RegionsVisible => _regionsVisible;

    public MapResourceController(IMapResourceJs resourceJs)
    {
        _resourceJs = resourceJs ?? throw new ArgumentNullException(nameof(resourceJs));
    }

    /// <summary>
    /// Push a fresh waypoint / note / region batch to the JS layer.
    /// Used at page init; the CRUD flow on Map.razor.cs pushes
    /// individual markers as the helm creates / deletes them.
    /// </summary>
    public async Task DrawAllAsync(
        IEnumerable<SignalkWaypoint> waypoints,
        IEnumerable<SignalkNote> notes,
        IEnumerable<SignalkRegion> regions)
    {
        // Waypoints have nullable lat/lon on the model; guard each
        // even though GetAllAsync usually filters position-less rows
        // out before they reach us.
        foreach (var wp in waypoints)
        {
            if (wp.Latitude is double lat && wp.Longitude is double lon)
            {
                await _resourceJs.AddWaypointMarkerAsync(
                    wp.Id, lat, lon, wp.Name,
                    wp.CreatedAt?.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        // Notes use a bare position (not a GeoJSON Feature);
        // GetAllAsync already filters out entries without a position
        // so the nullability check is just defensive.
        foreach (var n in notes)
        {
            if (n.Position is not null)
            {
                await _resourceJs.AddNoteMarkerAsync(
                    n.Id, n.Position.Latitude, n.Position.Longitude,
                    n.Title, n.Description,
                    n.CreatedAt?.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        // Region polygons: OuterRings is in Leaflet [lat, lon] order
        // already, one ring per polygon (MultiPolygon produces
        // multiple).
        foreach (var r in regions)
        {
            await _resourceJs.AddRegionAsync(
                r.Id, r.OuterRings, r.Name, r.Description,
                r.IsHazard,
                AreaForRings(r.OuterRings),
                r.CenterLat, r.CenterLon, r.RadiusMeters,
                FormatCreatedAt(r.CreatedAt));
        }
    }

    private static double AreaForRings(IReadOnlyList<double[][]> rings) =>
        rings is null || rings.Count == 0
            ? 0
            : OnaPlotter.Utilities.PolygonGeometry.AreaSquareMeters(rings[0]);

    private static string? FormatCreatedAt(DateTime? createdAt) =>
        createdAt is DateTime t
            ? t.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture)
            : null;

    // --- Per-resource redraw (delta-driven path) ---------------------
    //
    // Map.razor's HandleXxxChangedFromStore handlers call these when
    // a SignalK delta or REST reconcile lands a new / updated entry.
    // The pattern is remove-then-add: cheaper than the route-side
    // setLatLngs because waypoints / notes are single-point markers
    // (no geometry-update micro-flicker concern), and regions rebuild
    // their ring polygon either way. Add silently no-ops if the marker
    // already exists for a duplicate-add path; remove is idempotent.

    /// <summary>Re-render a waypoint marker after a store delta. No-op
    /// when the waypoint has no position (delta-fed waypoints with
    /// missing geometry already get filtered upstream by
    /// ResourceStore, but the guard is cheap and matches the batch
    /// path's defensive null-check).</summary>
    public async Task RedrawWaypointAsync(SignalkWaypoint wp)
    {
        if (wp.Latitude is not double lat || wp.Longitude is not double lon) return;
        await _resourceJs.RemoveWaypointMarkerAsync(wp.Id);
        await _resourceJs.AddWaypointMarkerAsync(
            wp.Id, lat, lon, wp.Name,
            wp.CreatedAt?.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Re-render a note marker after a store delta. Skipped
    /// while the notes layer is hidden - the helm hid them on
    /// purpose; a remote edit shouldn't pop them back into view.</summary>
    public async Task RedrawNoteAsync(SignalkNote note)
    {
        if (!_notesVisible) return;
        if (note.Position is null) return;
        await _resourceJs.RemoveNoteMarkerAsync(note.Id);
        await _resourceJs.AddNoteMarkerAsync(
            note.Id, note.Position.Latitude, note.Position.Longitude,
            note.Title, note.Description,
            note.CreatedAt?.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Re-render a region polygon after a store delta. Same
    /// hidden-layer guard as notes.</summary>
    public async Task RedrawRegionAsync(SignalkRegion region)
    {
        if (!_regionsVisible) return;
        if (region.OuterRings.Count == 0) return;
        await _resourceJs.RemoveRegionAsync(region.Id);
        await _resourceJs.AddRegionAsync(
            region.Id, region.OuterRings, region.Name, region.Description,
            region.IsHazard,
            AreaForRings(region.OuterRings),
            region.CenterLat, region.CenterLon, region.RadiusMeters,
            FormatCreatedAt(region.CreatedAt));
    }

    /// <summary>Drop a waypoint marker after a store-side delete.</summary>
    public Task DropWaypointAsync(string id) => _resourceJs.RemoveWaypointMarkerAsync(id);

    /// <summary>Drop a note marker after a store-side delete.</summary>
    public Task DropNoteAsync(string id) => _resourceJs.RemoveNoteMarkerAsync(id);

    /// <summary>Drop a region polygon after a store-side delete.</summary>
    public Task DropRegionAsync(string id) => _resourceJs.RemoveRegionAsync(id);

    /// <summary>
    /// Show / hide the notes layer. Hide tears down every marker via
    /// <c>clearNotes</c>; show re-pushes the supplied collection (the
    /// page passes its current loadedNotes since the controller
    /// doesn't own that state).
    /// </summary>
    public async Task SetNotesVisibleAsync(bool visible, IEnumerable<SignalkNote> notes)
    {
        _notesVisible = visible;
        if (visible)
        {
            foreach (var n in notes)
            {
                if (n.Position is null) continue;
                await _resourceJs.AddNoteMarkerAsync(
                    n.Id, n.Position.Latitude, n.Position.Longitude,
                    n.Title, n.Description,
                    n.CreatedAt?.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        else
        {
            await _resourceJs.ClearNotesAsync();
        }
    }

    /// <summary>Show / hide the regions layer. Same pattern as notes.</summary>
    public async Task SetRegionsVisibleAsync(bool visible, IEnumerable<SignalkRegion> regions)
    {
        _regionsVisible = visible;
        if (visible)
        {
            foreach (var r in regions)
            {
                await _resourceJs.AddRegionAsync(
                    r.Id, r.OuterRings, r.Name, r.Description,
                    r.IsHazard,
                    AreaForRings(r.OuterRings),
                    r.CenterLat, r.CenterLon, r.RadiusMeters,
                    FormatCreatedAt(r.CreatedAt));
            }
        }
        else
        {
            await _resourceJs.ClearRegionsAsync();
        }
    }
}
