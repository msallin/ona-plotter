using Microsoft.JSInterop;
using OnaPlotter.Models;
using OnaPlotter.Services;

namespace OnaPlotter.Components.Pages;

/// <summary>
/// Map-page partial: route-edit flow. Entry points are
/// <see cref="StartRouteEdit"/> (new route via the Add button) and
/// <see cref="EditRoute"/> (Edit button on a Layers-panel row).
/// Save pipelines through <see cref="SaveRouteCore"/> with a
/// re-entrancy guard shared with the polygon flow in
/// <c>Map.Editing.razor.cs</c>.
///
/// Every JS interop call catches <see cref="JSDisconnectedException"/>
/// so a page-switch mid-edit, or a server drop, doesn't surface a
/// torn-down-module exception to Blazor's error UI.
/// </summary>
public partial class Map
{
    // ---- Route editing state -------------------------------------------
    private bool routeEditMode;
    private string routeEditName = "";
    private string routeEditStats = "0 WP / 0 nm";
    private double[][]? routeEditCoords;
    private System.Threading.Timer? routeStatsTimer;
    // Id of the route currently being edited, if any. Null means this
    // is a fresh route ("Add Route" button); non-null means the user
    // entered the editor via the Layers panel's "Edit" button and
    // Save should PUT in place rather than POST a new resource.
    private string? routeEditId;

    // --- Route Editing --------------------------------------------------

    private async Task StartRouteEdit()
    {
        routeEditMode = true;
        routeEditId = null;                     // fresh route, not an in-place edit
        // Prefill with the same date-stamped default that Save falls back
        // to when the field is blank. Prefilled (instead of placeholder)
        // so iPad helms can see the name before tapping Save and edit it
        // in place, while the keyboard-shortcut "just save" path still
        // gets a sensible name without extra typing.
        routeEditName = $"Route {DateTime.Now:yyyyMMdd}";
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
            bool ok = await Confirmations.ConfirmAsync(
                $"Discard route in progress ({wpCount} waypoints)?");
            if (!ok) return;
        }
        if (wpCount > 0)
            Toasts.Info($"Route edit cancelled ({wpCount} waypoints discarded)");

        routeEditMode = false;
        routeEditId = null;
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

    /// <summary>JS-invokable entry point for the "Edit" button on the
    /// route popup (click on a route polyline -> Edit). Looks up the
    /// route by id and routes through the existing <see cref="EditRoute"/>
    /// flow so the popup path behaves identically to the Layers-panel
    /// Edit button.</summary>
    [JSInvokable]
    public async Task EditRouteById(string id)
    {
        var route = availableRoutes.FirstOrDefault(r => r.Id == id);
        if (route is null) { Toasts.Error("Route not found"); return; }
        await EditRoute(route);
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
        routeEditId = route.Id;                 // save will PUT in place
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
            string? existingId = routeEditId;
            // newRouteId captures the id either way:
            //   - edit flow: the same id we started with
            //   - fresh flow: the uuid the server returns in the
            //     POST response (via PostCreateAsync)
            // Used below to locate the route in the refreshed list
            // without a list-diff heuristic that was racy in
            // multi-client setups.
            string? newRouteId = null;
            string? errMsg = null;
            try
            {
                if (existingId is not null)
                {
                    var r = await RouteApi.UpdateAsync(existingId, name, coords);
                    ok = r.Success;
                    if (!ok) errMsg = r.Error;
                    if (ok) newRouteId = existingId;
                }
                else
                {
                    var r = await RouteApi.SaveAsync(name, coords);
                    ok = r.Success;
                    if (!ok) errMsg = r.Error;
                    if (ok) newRouteId = r.Value;
                }
            }
            catch (Exception ex) { Toasts.Error($"Save route failed: {ex.Message}"); ok = false; errMsg = ex.Message; }

            if (ok)
            {
                Toasts.Success($"Saved route '{name}' ({coords.Length} waypoints)");
                // Reload the list so the newly-saved route shows up
                // in Layers / search / route-switcher. The route
                // id we already have (from the server response or
                // the edit-in-place known id) is the source of
                // truth for finding the object; no list diff.
                availableRoutes = await SafeLoad(() => RouteApi.GetAllAsync(), "routes") ?? availableRoutes;
                PrecomputeRouteBounds();

                newRoute = !string.IsNullOrEmpty(newRouteId)
                    ? availableRoutes.FirstOrDefault(r => r.Id == newRouteId)
                    : null;

                if (existingId is not null && newRoute is not null && module is not null)
                {
                    // Edit-in-place redraw: wipe the old polyline
                    // so the geometry change takes effect.
                    try { await module.InvokeVoidAsync("removeRoute", existingId); }
                    catch (JSDisconnectedException) { }
                    catch (JSException) { /* next addRoute replaces it */ }
                    if (enabledRoutes.Contains(existingId))
                    {
                        try { await AddRouteToMap(newRoute); }
                        catch (JSDisconnectedException) { }
                        catch (JSException ex) { Toasts.Error($"Display route failed: {ex.Message}"); }
                    }
                }
                else if (existingId is null && newRoute is not null && !string.IsNullOrEmpty(newRoute.Id))
                {
                    // Fresh save: enable and draw on the map.
                    enabledRoutes.Add(newRoute.Id);
                    await Settings.SetEnabledRoutesAsync(enabledRoutes);
                    try { await AddRouteToMap(newRoute); }
                    catch (JSDisconnectedException) { }
                    catch (JSException ex) { Toasts.Error($"Display route failed: {ex.Message}"); }
                }
                RebuildFilteredLayers();
            }
            else if (Toasts.Active.Count == 0) // only if we haven't already toasted the exception
            {
                Toasts.Error(string.IsNullOrWhiteSpace(errMsg)
                    ? "Failed to save route"
                    : $"Failed to save route: {errMsg}");
            }
        }
        finally
        {
            // Tear down the edit-mode state regardless of how the save
            // unwound. Previously a JS exception above left the route-
            // edit overlay + poll timer + nav-guard installed, and the
            // user's next interaction landed on a ghost edit session.
            routeEditMode = false;
            routeEditId = null;
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
                var started = await CourseApi.SetActiveRouteAsync(newRoute.Id);
                if (started.Success)
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
                    Toasts.Error($"Route saved but couldn't start navigation: {started.Error ?? "tap the route in Layers to activate"}");
                }
            }
            catch (Exception ex)
            {
                Toasts.Error($"Route saved but start navigation failed: {ex.Message}");
            }
        }
    }
}
