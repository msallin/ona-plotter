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
                await _resourceJs.AddWaypointMarkerAsync(wp.Id, lat, lon, wp.Name);
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
                    n.Id, n.Position.Latitude, n.Position.Longitude, n.Title, n.Description);
            }
        }
        // Region polygons: OuterRings is in Leaflet [lat, lon] order
        // already, one ring per polygon (MultiPolygon produces
        // multiple).
        foreach (var r in regions)
        {
            await _resourceJs.AddRegionAsync(r.Id, r.OuterRings, r.Name, r.Description);
        }
    }

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
                    n.Id, n.Position.Latitude, n.Position.Longitude, n.Title, n.Description);
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
                await _resourceJs.AddRegionAsync(r.Id, r.OuterRings, r.Name, r.Description);
            }
        }
        else
        {
            await _resourceJs.ClearRegionsAsync();
        }
    }
}
