using Microsoft.JSInterop;
using OnaPlotter.Models;
using OnaPlotter.Services;

namespace OnaPlotter.Components.Pages;

/// <summary>
/// Map page code-behind partial: route + polygon-region edit flows.
/// Both edit modes share the same shape (start -> user paints on the
/// map -> stats poll + in-panel list -> save or cancel) so they live
/// together here. ~450 lines lifted out of Map.razor to keep the main
/// file focused on lifecycle + layout.
///
/// Shared helpers:
///   - <c>InstallEditNavGuard</c> / <c>RemoveEditNavGuard</c>: block
///     in-app navigation (switch to another page) while an edit is
///     active. Browser-level close/refresh is handled JS-side by the
///     <c>startRouteEdit</c> / <c>startPolygonEdit</c> module calls.
///   - <c>CoordsEqual</c>: shallow double[][] comparison used by both
///     UpdateRouteStats and UpdatePolygonStats to skip re-renders
///     when the poll tick returns unchanged coords.
/// </summary>
public partial class Map
{
    // ---- Route editing state -------------------------------------------
    private bool routeEditMode;
    private string routeEditName = "";
    private string routeEditStats = "0 WP / 0 nm";
    private double[][]? routeEditCoords;
    private System.Threading.Timer? routeStatsTimer;

    // ---- Polygon editing state -----------------------------------------
    private bool polygonEditMode;
    private string polygonEditName = "";
    private string polygonEditStats = "0 vertices";
    private double[][]? polygonEditCoords;
    private System.Threading.Timer? polygonStatsTimer;

    // Re-entrancy guard on Save / Save & Go. Without this, a double-tap
    // posts two identical routes + activates both. Silent re-entry is
    // the intended behaviour; the disabled button styling in the panel
    // is the visible affordance.
    private bool _saveInFlight;

    // Registered while route-edit OR polygon-edit is active. Router
    // fires LocationChanging before committing nav; we intercept and
    // ask the helm to confirm.
    private IDisposable? editNavGuard;

    // --- Route Editing --------------------------------------------------

    private async Task StartRouteEdit()
    {
        routeEditMode = true;
        routeEditName = "";
        routeEditStats = "0 WP / 0 nm";
        InstallEditNavGuard();
        if (module is not null)
            await module.InvokeVoidAsync("startRouteEdit");
        // Poll stats twice a second while editing.
        routeStatsTimer = new System.Threading.Timer(
            _ => _ = UpdateRouteStats(), null,
            RouteStatsPollIntervalMs, RouteStatsPollIntervalMs);
    }

    private async Task CancelRouteEdit()
    {
        // Discarding significant work should require intent. 2+ waypoints
        // is the threshold where the helm has genuinely built something
        // (single point is just a tap, two points is a line they'd rather
        // not lose). Uses the native confirm() dialog -- same pattern as
        // the "Remove polar?" prompt in Settings. Empty / single-point
        // edits skip the prompt so Esc-to-bail still feels immediate.
        int wpCount = routeEditCoords?.Length ?? 0;
        if (wpCount >= 2)
        {
            bool ok;
            try
            {
                ok = await JS.InvokeAsync<bool>("confirm",
                    $"Discard route in progress ({wpCount} waypoints)?");
            }
            catch (JSDisconnectedException) { return; }
            if (!ok) return;
        }
        if (wpCount > 0)
            Toasts.Info($"Route edit cancelled ({wpCount} WP discarded)");

        routeEditMode = false;
        routeEditCoords = null;
        routeStatsTimer?.Dispose();
        routeStatsTimer = null;
        RemoveEditNavGuard();
        if (module is not null)
            await module.InvokeVoidAsync("stopRouteEdit");
    }

    private async Task UndoLastWaypoint()
    {
        if (module is not null)
            await module.InvokeVoidAsync("undoLastEditWaypoint");
        await UpdateRouteStats();
    }

    private async Task EditRoute(SignalkRoute route)
    {
        if (module is null || route.Feature?.Geometry is null) return;

        // Convert GeoJSON [lon, lat] to Leaflet [lat, lon].
        var coords = new List<double[]>();
        foreach (var point in route.Feature.Geometry.Coordinates.EnumerateArray())
        {
            var arr = new double[2];
            int i = 0;
            foreach (var val in point.EnumerateArray())
            {
                if (i < 2) arr[i++] = val.GetDouble();
            }
            coords.Add([arr[1], arr[0]]);
        }

        routeEditMode = true;
        routeEditName = route.Name ?? "";
        chartPanelOpen = false; // Close layers panel so user can see the map.
        InstallEditNavGuard();
        await module.InvokeVoidAsync("loadRouteForEdit", (object)coords.ToArray());
        routeStatsTimer = new System.Threading.Timer(
            _ => _ = UpdateRouteStats(), null,
            RouteStatsPollIntervalMs, RouteStatsPollIntervalMs);
        await UpdateRouteStats();
    }

    private async Task UpdateRouteStats()
    {
        if (module is null) return;
        try
        {
            // Fetch stats + coords together so the in-panel waypoint list
            // stays in sync with the polyline. Both round-trips are cheap
            // (JS-side arrays), but we still only do one render afterwards.
            var stats = await module.InvokeAsync<double[]>("getEditRouteStats");
            var coords = await module.InvokeAsync<double[][]>("getEditRouteCoords");
            bool dirty = false;
            if (stats is not null && stats.Length == 2)
            {
                int wpCount = (int)stats[0];
                double nm = stats[1];
                var s = $"{wpCount} WP / {nm:F1} nm";
                if (s != routeEditStats) { routeEditStats = s; dirty = true; }
            }
            if (coords is not null && !CoordsEqual(coords, routeEditCoords))
            {
                routeEditCoords = coords;
                dirty = true;
            }
            if (dirty) await InvokeAsync(StateHasChanged);
        }
        catch (JSDisconnectedException) { }
        catch (ObjectDisposedException) { }
    }

    // Shallow check so we don't render the whole panel 2 times/second
    // just because the polling loop fired; a drag will almost always
    // change one row and we want to re-render then, but if nothing moved
    // we don't.
    private static bool CoordsEqual(double[][]? a, double[][]? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i].Length != b[i].Length) return false;
            for (int j = 0; j < a[i].Length; j++)
                if (a[i][j] != b[i][j]) return false;
        }
        return true;
    }

    private async Task RemoveRouteWaypoint(int index)
    {
        if (module is null) return;
        try { await module.InvokeVoidAsync("removeRouteEditWaypoint", index); }
        catch (JSDisconnectedException) { return; }
        await UpdateRouteStats();
    }

    // Save + optional auto-activate are the same pipeline; SaveRoute
    // just stops there, SaveRouteAndGo threads `activate:true` through
    // so the Layers-panel detour isn't needed for the single most
    // common follow-up step.
    private Task SaveRoute() => SaveRouteCore(activate: false);
    private Task SaveRouteAndGo() => SaveRouteCore(activate: true);

    private async Task SaveRouteCore(bool activate)
    {
        if (module is null) return;
        if (_saveInFlight) return;
        _saveInFlight = true;
        try
        {
            await SaveRouteCoreInner(activate);
        }
        finally
        {
            _saveInFlight = false;
            try { await InvokeAsync(StateHasChanged); }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task SaveRouteCoreInner(bool activate)
    {
        if (module is null) return;

        double[][]? coords = null;
        try { coords = await module.InvokeAsync<double[][]>("getEditRouteCoords"); }
        catch (JSDisconnectedException) { /* cleanup runs in finally */ }
        catch (JSException ex) { Toasts.Error($"Couldn't read route: {ex.Message}"); }

        SignalkRoute? newRoute = null;
        bool ok = false;
        try
        {
            if (coords is null || coords.Length < 2)
            {
                // Save button is now disabled in this state, but belt-and-braces
                // in case a race gets us here anyway (keyboard shortcut, JS
                // interop lag). Toast so the helm knows WHY nothing saved.
                if (coords is not null)
                    Toasts.Show("Add at least 2 waypoints before saving");
                return;
            }
            // Empty name -> date-stamped default. yyyyMMdd is monotonically
            // sortable in the routes list, which matters more than time-of-day
            // (most users create a few routes per day; "Route 14:30" starts
            // to look identical after a week).
            string name = string.IsNullOrWhiteSpace(routeEditName)
                ? $"Route {DateTime.Now:yyyyMMdd}"
                : routeEditName;
            try { ok = await RouteApi.SaveAsync(name, coords); }
            catch (Exception ex) { Toasts.Error($"Save route failed: {ex.Message}"); ok = false; }

            if (ok)
            {
                Toasts.Success($"Saved route '{name}' ({coords.Length} WP)");
                // Snapshot pre-save IDs so we can auto-enable the newly-created
                // route. SignalK's POST response body varies between server
                // versions (some return { "id": ... }, some just 201 Created),
                // so a diff against the previous list is more robust than
                // parsing the response. Limitation: in a multi-client
                // scenario (another plotter saves a route during our same
                // window) this can grab that route instead; in practice
                // Save & Go then activates the wrong one. Acceptable for
                // a solo-plotter workflow; the fix is server-side (return
                // the id in the POST body, which SK Node Server does in
                // v2 -- pending upgrade).
                var prevIds = availableRoutes.Select(r => r.Id).ToHashSet();
                availableRoutes = await SafeLoad(() => RouteApi.GetAllAsync(), "routes") ?? availableRoutes;
                PrecomputeRouteBounds();
                // When multiple "new" ids appear, prefer the one whose name
                // matches ours -- still heuristic, but tighter than "first
                // in list".
                var candidates = availableRoutes
                    .Where(r => !string.IsNullOrEmpty(r.Id) && !prevIds.Contains(r.Id))
                    .ToList();
                newRoute = candidates.FirstOrDefault(r =>
                    string.Equals(r.Name, name, StringComparison.Ordinal))
                    ?? candidates.FirstOrDefault();
                if (newRoute is not null && !string.IsNullOrEmpty(newRoute.Id))
                {
                    enabledRoutes.Add(newRoute.Id);
                    await Settings.SetEnabledRoutesAsync(enabledRoutes);
                }
                RebuildFilteredLayers();
            }
            else if (Toasts.Active.Count == 0) // only if we haven't already toasted the exception
            {
                Toasts.Error("Failed to save route");
            }
        }
        finally
        {
            // Tear down the edit-mode state regardless of how the save
            // unwound. Previously a JS exception above left the route-
            // edit overlay + poll timer + nav-guard installed, and the
            // user's next interaction landed on a ghost edit session.
            routeEditMode = false;
            routeEditCoords = null;
            routeStatsTimer?.Dispose();
            routeStatsTimer = null;
            RemoveEditNavGuard();
            try { await module.InvokeVoidAsync("stopRouteEdit"); }
            catch (JSDisconnectedException) { }
            catch (JSException) { /* JS already torn down; overlay will go on next init */ }
        }

        // Save+Go: activate the route the user just created so the
        // next heartbeat lights up the course HUD / alarms / leg line.
        // Deliberately after the stopRouteEdit call so the edit overlay
        // is gone by the time the active-route overlay appears.
        if (activate && ok && newRoute is not null)
        {
            try
            {
                bool startedOk = await CourseApi.SetActiveRouteAsync(newRoute.Id);
                if (startedOk)
                {
                    Toasts.Success($"Navigating route '{newRoute.Name ?? newRoute.Id}'");
                }
                else
                {
                    // Save succeeded but activation didn't. Route is in
                    // the Layers list, just not the active course. Tell
                    // the sailor what happened AND give them the one-tap
                    // retry via the Layers panel so they don't have to
                    // dig for it.
                    Toasts.Error("Route saved but couldn't start navigation -- tap the route in Layers to activate");
                }
            }
            catch (Exception ex)
            {
                Toasts.Error($"Route saved but start navigation failed: {ex.Message}");
            }
        }
    }

    // --- Polygon-region editing ------------------------------------
    // Same poll cadence as route editing; reuses RouteStatsPollIntervalMs.
    // Every JS interop call catches JSDisconnectedException so a page
    // switch mid-edit (fast nav, server disconnect) doesn't surface a
    // torn-down-module exception to Blazor's error UI. See agents.md.
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
        string? id;
        try { id = await RegionApi.CreatePolygonAsync(name, "", coords); }
        catch (Exception ex) { Toasts.Error($"Save region failed: {ex.Message}"); id = null; }

        if (!string.IsNullOrEmpty(id))
        {
            Toasts.Success($"Saved region '{name}' ({coords.Length} vertices)");
            loadedRegions = await SafeLoad(() => RegionApi.GetAllAsync(), "regions") ?? loadedRegions;
            RebuildFilteredLayers();
        }
        else if (Toasts.Active.Count == 0)
        {
            Toasts.Error("Failed to save region");
        }
        polygonEditMode = false;
        polygonEditCoords = null;
        polygonStatsTimer?.Dispose();
        polygonStatsTimer = null;
        RemoveEditNavGuard();
        try { await module.InvokeVoidAsync("stopPolygonEdit"); }
        catch (JSDisconnectedException) { }
    }

    // --- unsaved-changes guard for in-app navigation -----------------
    // Registered while route-edit or polygon-edit is active so switching
    // to Dashboard / Settings / etc. prompts before discarding the edit.
    // Browser-level navigation (close tab, back) is handled JS-side via
    // beforeunload in the same `startRouteEdit` / `startPolygonEdit`
    // module calls. RegisterLocationChangingHandler returns IDisposable;
    // disposing cancels the registration.

    private void InstallEditNavGuard()
    {
        if (editNavGuard is not null) return;
        editNavGuard = Nav.RegisterLocationChangingHandler(async ctx =>
        {
            if (!routeEditMode && !polygonEditMode) return;
            bool ok;
            try
            {
                ok = await JS.InvokeAsync<bool>("confirm",
                    "You have an unsaved edit. Leave and discard it?");
            }
            catch (JSDisconnectedException) { return; }
            if (!ok) ctx.PreventNavigation();
        });
    }

    private void RemoveEditNavGuard()
    {
        editNavGuard?.Dispose();
        editNavGuard = null;
    }
}
