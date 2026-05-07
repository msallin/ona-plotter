using Microsoft.JSInterop;

namespace OnaPlotter.Components.Pages;

/// <summary>
/// Map-page partial: polygon-region edit flow. Starts via
/// <see cref="StartPolygonEdit"/> from the Add button's "Region (polygon)"
/// option; ends via <see cref="SavePolygonRegion"/> or
/// <see cref="CancelPolygonEdit"/>. Shares the nav-guard plumbing +
/// CoordsEqual helper in <c>Map.Editing.razor.cs</c> with the route
/// flow and the <c>polygonEditMode</c> state machine lives here
/// so the route file stays focused on its own lifecycle.
///
/// Poll cadence reuses <c>RouteStatsPollIntervalMs</c> from the
/// main Map page constants. Interop goes through the typed
/// <c>IMapEditJs</c> / <c>IMapResourceJs</c> wrappers; lifecycle
/// exceptions are absorbed inside the wrapper.
/// </summary>
public partial class Map
{
    // ---- Polygon editing state -----------------------------------------
    private bool polygonEditMode;
    private string polygonEditName = "";
    private string polygonEditStats = "0 vertices";
    private double[][]? polygonEditCoords;
    private System.Threading.Timer? polygonStatsTimer;
    // Id of the region currently being edited, or null when drawing a
    // fresh one. Non-null means Save should PUT in place rather than
    // POST a new region. Mirrors routeEditId.
    private string? polygonEditId;
    // Description carried across Save for edit flows. The new-region
    // dialog drops its entered description into newRegionDescription;
    // edit flows stash the existing region's description here.
    private string polygonEditDescription = "";
    // Hazard flag carried through edit -> save. New regions start
    // false (decorative); edit flows seed from the existing region's
    // IsHazard so a re-save preserves the flag without forcing the
    // helm to re-tick it. Bound to the panel's `IsHazard` parameter.
    private bool polygonEditIsHazard;
    // Originals carried through edit -> save so the resources-fs full-
    // replacement PUT preserves them. New regions leave them null
    // (RegionApi.CreatePolygonAsync stamps a fresh createdAt; circle
    // metadata stays null for freeform polygons). Edit flows seed
    // from the region the helm picked.
    private DateTime? polygonEditCreatedAt;
    private double? polygonEditCenterLat;
    private double? polygonEditCenterLon;
    private double? polygonEditRadiusMeters;

    private async Task StartPolygonEdit()
    {
        // Mutually exclusive with route editing.
        if (routeEditMode) await CancelRouteEdit();
        polygonEditMode = true;
        polygonEditName = "";
        polygonEditStats = "0 vertices";
        polygonEditCoords = null;
        polygonEditId = null;
        polygonEditDescription = "";
        polygonEditIsHazard = false;
        polygonEditCreatedAt = null;
        polygonEditCenterLat = null;
        polygonEditCenterLon = null;
        polygonEditRadiusMeters = null;
        InstallEditNavGuard();
        if (_editJs is not null)
            await _editJs.StartPolygonEditAsync();
        polygonStatsTimer = new System.Threading.Timer(
            _ => _ = UpdatePolygonStats(), null,
            RouteStatsPollIntervalMs, RouteStatsPollIntervalMs);
    }

    /// <summary>Open an existing region for editing. Loads the first
    /// outer ring into polygon-edit mode so the user can drag vertices
    /// / insert / remove; Save PUTs the update in place (see
    /// <see cref="SavePolygonRegion"/>). Regions with multiple rings
    /// surface a toast - we don't yet have a multi-ring UI.</summary>
    private async Task EditRegion(Models.SignalkRegion region)
    {
        if (_editJs is null) return;
        if (region.OuterRings.Count == 0)
        {
            Toasts.Error("Region has no editable geometry");
            return;
        }
        if (region.OuterRings.Count > 1)
        {
            Toasts.Warning("Editing the first ring only (multi-ring not yet supported)");
        }
        if (routeEditMode) await CancelRouteEdit();
        // A polygon edit already in progress (user was drawing a
        // fresh region, then clicked Edit on an existing one in the
        // Layers panel) leaves the old polygonStatsTimer running
        // against a torn-down Leaflet state. Cancel the prior session
        // cleanly so the timer is disposed and the nav guard
        // reference-count stays balanced.
        if (polygonEditMode) await CancelPolygonEdit();
        // Close the Layers panel so the chart is visible while editing.
        chartPanelOpen = false;
        polygonEditMode = true;
        polygonEditName = region.Name ?? "";
        polygonEditStats = "0 vertices";
        polygonEditCoords = null;
        polygonEditId = region.Id;
        polygonEditDescription = region.Description ?? "";
        polygonEditIsHazard = region.IsHazard;
        polygonEditCreatedAt = region.CreatedAt;
        polygonEditCenterLat = region.CenterLat;
        polygonEditCenterLon = region.CenterLon;
        polygonEditRadiusMeters = region.RadiusMeters;
        InstallEditNavGuard();
        // The stored ring is closed (last point == first); drop the
        // duplicate before seeding the edit vertices so the user
        // doesn't see a zero-length segment at vertex N.
        var ring = region.OuterRings[0];
        var drawn = ring.Length > 1
            && ring[0].Length >= 2 && ring[^1].Length >= 2
            && ring[0][0] == ring[^1][0] && ring[0][1] == ring[^1][1]
            ? ring[..^1]
            : ring;
        await _editJs.LoadPolygonForEditAsync(drawn);
        polygonStatsTimer = new System.Threading.Timer(
            _ => _ = UpdatePolygonStats(), null,
            RouteStatsPollIntervalMs, RouteStatsPollIntervalMs);
    }

    private async Task CancelPolygonEdit()
    {
        polygonEditMode = false;
        polygonEditCoords = null;
        polygonEditId = null;
        polygonEditDescription = "";
        polygonEditIsHazard = false;
        polygonEditCreatedAt = null;
        polygonEditCenterLat = null;
        polygonEditCenterLon = null;
        polygonEditRadiusMeters = null;
        newRegionDescription = "";
        polygonStatsTimer?.Dispose();
        polygonStatsTimer = null;
        RemoveEditNavGuard();
        if (_editJs is not null)
            await _editJs.StopPolygonEditAsync();
    }

    private async Task UndoLastPolygonVertex()
    {
        if (_editJs is null) return;
        await _editJs.UndoLastPolygonVertexAsync();
        await UpdatePolygonStats();
    }

    private async Task UpdatePolygonStats()
    {
        if (_editJs is null) return;
        var coords = await _editJs.GetPolygonEditCoordsAsync();
        bool dirty = false;
        if (coords is not null)
        {
            int n = coords.Length;
            // Vertex count + area both derive from the coords we
            // just fetched, so the C# side computes them directly
            // instead of round-tripping a second JS interop call.
            // PolygonGeometry returns 0 below 3 vertices.
            double area = OnaPlotter.Utilities.PolygonGeometry.AreaSquareMeters(coords);
            var s = n < 3
                ? $"{n} vertices"
                : $"{n} vertices / {FormatArea(area)}";
            if (s != polygonEditStats) { polygonEditStats = s; dirty = true; }
            if (!CoordsEqual(coords, polygonEditCoords))
            {
                polygonEditCoords = coords;
                dirty = true;
            }
        }
        if (dirty) await InvokeAsync(StateHasChanged);
    }

    // Metric area formatter. Under 1 ha show square metres; above,
    // switch to hectares or km^2 - matches what sailors expect for
    // anchorage / no-go zones.
    private static string FormatArea(double squareMeters)
    {
        if (squareMeters < 10_000) return $"{squareMeters:F0} m\u00B2";
        if (squareMeters < 1_000_000) return $"{squareMeters / 10_000:F2} ha";
        return $"{squareMeters / 1_000_000:F2} km\u00B2";
    }

    private async Task RemovePolygonVertex(int index)
    {
        if (_editJs is null) return;
        await _editJs.RemovePolygonEditVertexAsync(index);
        await UpdatePolygonStats();
    }

    private async Task SavePolygonRegion()
    {
        if (_editJs is null || _resourceJs is null) return;
        double[][]? coords = await _editJs.GetPolygonEditCoordsAsync();
        if (coords is null || coords.Length < 3)
        {
            Toasts.Show("Add at least 3 vertices before saving");
            // Defer the full state reset to CancelPolygonEdit so any
            // future field added to the cancel path (e.g. a new edit-
            // mode flag) automatically applies here too. Without this
            // the abort path would leave polygonEditId populated from
            // the prior edit, which would make the next Save PUT over
            // that old region instead of creating a fresh one.
            await CancelPolygonEdit();
            return;
        }
        string name = string.IsNullOrWhiteSpace(polygonEditName)
            ? $"Region {DateTime.Now:yyyyMMdd-HHmm}"
            : polygonEditName;
        // New region: description comes from the Add-Region dialog
        // (newRegionDescription). Edit: description is the one the
        // region already had, stashed in polygonEditDescription at
        // EditRegion time. The edit-bar doesn't currently expose a
        // description field; if we add one later, polygonEditDescription
        // becomes the bound state.
        string description = polygonEditId is null
            ? (newRegionDescription ?? "")
            : polygonEditDescription;

        string? savedId = null;
        string? failReason = null;
        try
        {
            if (polygonEditId is string editingId)
            {
                // In-place update of an existing region; thread the
                // hazard flag + createdAt + circle metadata so a
                // re-save preserves them across the resources-fs
                // full-replacement PUT.
                var upd = await RegionApi.UpdatePolygonAsync(
                    editingId, name, description, coords, polygonEditIsHazard,
                    createdAt: polygonEditCreatedAt,
                    centerLat: polygonEditCenterLat,
                    centerLon: polygonEditCenterLon,
                    radiusMeters: polygonEditRadiusMeters);
                if (upd.Success) savedId = editingId;
                else failReason = upd.Error ?? "server rejected";
            }
            else
            {
                // Fresh region.
                var create = await RegionApi.CreatePolygonAsync(
                    name, description, coords, polygonEditIsHazard);
                if (create is { Success: true, Value: { Length: > 0 } }) savedId = create.Value;
                else failReason = create?.Error ?? "server rejected";
            }
        }
        catch (Exception ex) { failReason = ex.Message; }

        if (savedId is not null)
        {
            Toasts.Success(polygonEditId is null
                ? $"Saved region '{name}' ({coords.Length} vertices)"
                : $"Updated region '{name}' ({coords.Length} vertices)");
            loadedRegions = await SafeLoad(() => RegionApi.GetAllAsync(), "regions") ?? loadedRegions;
            // Re-draw on the map. For edits, the existing Leaflet layer
            // still shows the old polygon; remove it first before adding
            // the updated one so we don't stack two overlapping shapes.
            if (polygonEditId is string editedId)
                await _resourceJs.RemoveRegionAsync(editedId);
            var saved = loadedRegions.FirstOrDefault(rg => rg.Id == savedId);
            if (saved is not null)
            {
                await _resourceJs.AddRegionAsync(
                    saved.Id, saved.OuterRings, saved.Name, saved.Description,
                    saved.IsHazard,
                    AreaForRings(saved.OuterRings),
                    saved.CenterLat, saved.CenterLon, saved.RadiusMeters,
                    FormatCreatedAt(saved.CreatedAt));
            }
            RebuildFilteredLayers();
        }
        else
        {
            Toasts.Error($"Save region failed: {failReason}");
        }
        polygonEditMode = false;
        polygonEditCoords = null;
        polygonEditId = null;
        polygonEditDescription = "";
        polygonEditIsHazard = false;
        polygonEditCreatedAt = null;
        polygonEditCenterLat = null;
        polygonEditCenterLon = null;
        polygonEditRadiusMeters = null;
        polygonStatsTimer?.Dispose();
        polygonStatsTimer = null;
        newRegionDescription = "";
        RemoveEditNavGuard();
        await _editJs.StopPolygonEditAsync();
    }

    /// <summary>Square metres of the FIRST outer ring of a region.
    /// Multi-ring regions only render the first; the popup metric
    /// follows the same scope so the displayed area matches the
    /// drawn polygon. Returns 0 for an empty list (the popup then
    /// hides the area row rather than showing "0 m^2").</summary>
    private static double AreaForRings(IReadOnlyList<double[][]> rings)
    {
        if (rings is null || rings.Count == 0) return 0;
        return OnaPlotter.Utilities.PolygonGeometry.AreaSquareMeters(rings[0]);
    }

    /// <summary>ISO-8601 UTC string for a region's createdAt (or null
    /// for regions that don't carry one). Same shape the note +
    /// waypoint markers use so the JS-side popup formatter is
    /// shared.</summary>
    private static string? FormatCreatedAt(DateTime? createdAt) =>
        createdAt is DateTime t
            ? t.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture)
            : null;
}
