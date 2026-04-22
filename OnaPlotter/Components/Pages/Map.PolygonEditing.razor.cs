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

    private async Task StartPolygonEdit()
    {
        // Mutually exclusive with route editing.
        if (routeEditMode) await CancelRouteEdit();
        polygonEditMode = true;
        polygonEditName = "";
        polygonEditStats = "0 vertices";
        polygonEditCoords = null;
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

    private async Task CancelPolygonEdit()
    {
        polygonEditMode = false;
        polygonEditCoords = null;
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
            var stats = await module.InvokeAsync<double[]>("getPolygonEditStats");
            var coords = await module.InvokeAsync<double[][]>("getPolygonEditCoords");
            bool dirty = false;
            if (stats is not null && stats.Length == 2)
            {
                int n = (int)stats[0];
                double area = stats[1];
                var s = n < 3
                    ? $"{n} vertices"
                    : $"{n} vertices / {FormatArea(area)}";
                if (s != polygonEditStats) { polygonEditStats = s; dirty = true; }
            }
            if (coords is not null && !CoordsEqual(coords, polygonEditCoords))
            {
                polygonEditCoords = coords;
                dirty = true;
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
            polygonEditMode = false;
            polygonEditCoords = null;
            polygonStatsTimer?.Dispose();
            polygonStatsTimer = null;
            RemoveEditNavGuard();
            try { await module.InvokeVoidAsync("stopPolygonEdit"); }
            catch (JSDisconnectedException) { }
            return;
        }
        string name = string.IsNullOrWhiteSpace(polygonEditName)
            ? $"Region {DateTime.Now:yyyyMMdd-HHmm}"
            : polygonEditName;
        Services.Api.ApiResult<string>? r = null;
        try { r = await RegionApi.CreatePolygonAsync(name, "", coords); }
        catch (Exception ex) { Toasts.Error($"Save region failed: {ex.Message}"); }

        if (r is { Success: true, Value: { Length: > 0 } })
        {
            Toasts.Success($"Saved region '{name}' ({coords.Length} vertices)");
            loadedRegions = await SafeLoad(() => RegionApi.GetAllAsync(), "regions") ?? loadedRegions;
            RebuildFilteredLayers();
        }
        else if (Toasts.Active.Count == 0)
        {
            Toasts.Error($"Save region failed: {r?.Error ?? "server rejected"}");
        }
        polygonEditMode = false;
        polygonEditCoords = null;
        polygonStatsTimer?.Dispose();
        polygonStatsTimer = null;
        RemoveEditNavGuard();
        try { await module.InvokeVoidAsync("stopPolygonEdit"); }
        catch (JSDisconnectedException) { }
    }
}
