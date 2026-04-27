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
/// main Map page constants. Every JS interop call catches
/// <see cref="JSDisconnectedException"/> so a page switch mid-edit
/// doesn't surface a torn-down-module exception to Blazor's error
/// UI.
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
        InstallEditNavGuard();
        if (module is not null)
        {
            try { await module.InvokeVoidAsync("startPolygonEdit"); }
            catch (JSDisconnectedException) { return; }
        }
        polygonStatsTimer = new System.Threading.Timer(
            _ => _ = UpdatePolygonStats(), null,
            RouteStatsPollIntervalMs, RouteStatsPollIntervalMs);
    }

    /// <summary>Open an existing region for editing. Loads the first
    /// outer ring into polygon-edit mode so the user can drag vertices
    /// / insert / remove; Save PUTs the update in place (see
    /// <see cref="SavePolygonRegion"/>). Regions with multiple rings
    /// surface a toast -- we don't yet have a multi-ring UI.</summary>
    private async Task EditRegion(Models.SignalkRegion region)
    {
        if (module is null) return;
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
        try { await module.InvokeVoidAsync("loadPolygonForEdit", (object)drawn); }
        catch (JSDisconnectedException) { return; }
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
        newRegionDescription = "";
        polygonStatsTimer?.Dispose();
        polygonStatsTimer = null;
        RemoveEditNavGuard();
        if (module is not null)
        {
            try { await module.InvokeVoidAsync("stopPolygonEdit"); }
            catch (JSDisconnectedException) { }
        }
    }

    private async Task UndoLastPolygonVertex()
    {
        if (module is null) return;
        try { await module.InvokeVoidAsync("undoLastPolygonVertex"); }
        catch (JSDisconnectedException) { return; }
        await UpdatePolygonStats();
    }

    private async Task UpdatePolygonStats()
    {
        if (module is null) return;
        try
        {
            var coords = await module.InvokeAsync<double[][]>("getPolygonEditCoords");
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
        catch (JSDisconnectedException) { }
        catch (ObjectDisposedException) { }
    }

    // Metric area formatter. Under 1 ha show square metres; above,
    // switch to hectares or km^2 -- matches what sailors expect for
    // anchorage / no-go zones.
    private static string FormatArea(double squareMeters)
    {
        if (squareMeters < 10_000) return $"{squareMeters:F0} m\u00B2";
        if (squareMeters < 1_000_000) return $"{squareMeters / 10_000:F2} ha";
        return $"{squareMeters / 1_000_000:F2} km\u00B2";
    }

    private async Task RemovePolygonVertex(int index)
    {
        if (module is null) return;
        try { await module.InvokeVoidAsync("removePolygonEditVertex", index); }
        catch (JSDisconnectedException) { return; }
        await UpdatePolygonStats();
    }

    private async Task SavePolygonRegion()
    {
        if (module is null) return;
        double[][]? coords;
        try { coords = await module.InvokeAsync<double[][]>("getPolygonEditCoords"); }
        catch (JSDisconnectedException) { return; }
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
                // In-place update of an existing region.
                var upd = await RegionApi.UpdatePolygonAsync(editingId, name, description, coords);
                if (upd.Success) savedId = editingId;
                else failReason = upd.Error ?? "server rejected";
            }
            else
            {
                // Fresh region.
                var create = await RegionApi.CreatePolygonAsync(name, description, coords);
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
            {
                try { await module.InvokeVoidAsync("removeRegion", editedId); }
                catch (JSDisconnectedException) { }
            }
            var saved = loadedRegions.FirstOrDefault(rg => rg.Id == savedId);
            if (saved is not null)
            {
                try
                {
                    await module.InvokeVoidAsync("addRegion",
                        saved.Id, saved.OuterRings, saved.Name, saved.Description);
                }
                catch (JSDisconnectedException) { }
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
        polygonStatsTimer?.Dispose();
        polygonStatsTimer = null;
        newRegionDescription = "";
        RemoveEditNavGuard();
        try { await module.InvokeVoidAsync("stopPolygonEdit"); }
        catch (JSDisconnectedException) { }
    }
}
