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
    // Name the route had at edit-start. SaveAsCopy uses this to decide
    // whether to auto-append " (copy)" to the new route's name -- if
    // the helm has already typed a different name, leave it alone;
    // only append when the field still shows the source route's name.
    private string? routeEditOriginalName;

    // --- Route Editing --------------------------------------------------

    private async Task StartRouteEdit()
    {
        // Close the Add flyout when the user picks Route -- the menu
        // stayed open on entry to edit mode and then floated on top of
        // the edit panel. Matches the FabCreate* callbacks which all
        // set fabMenuOpen=false first.
        fabMenuOpen = false;
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
            // Action-verb labels instead of the default "Cancel" /
            // "Confirm": the route-edit bar already has a "Cancel"
            // button, so a modal with "Cancel" and "Confirm" reads
            // ambiguous ("which Cancel am I clicking?"). Explicit
            // verbs match the question so no helm can click the
            // wrong one under stress.
            bool ok = await Confirmations.ConfirmAsync(
                $"Discard route in progress ({wpCount} waypoints)?",
                confirmLabel: "Discard",
                cancelLabel: "Keep editing");
            if (!ok) return;
        }
        if (wpCount > 0)
            Toasts.Info($"Route edit cancelled ({wpCount} waypoints discarded)");

        routeEditMode = false;
        routeEditId = null;
        routeEditOriginalName = null;
        routeEditCoords = null;
        routeStatsTimer?.Dispose();
        routeStatsTimer = null;
        RemoveEditNavGuard();
        if (module is not null)
            await module.InvokeVoidAsync("stopRouteEdit");

        // If we were editing the active route, EditRoute hid the
        // active-route overlay to avoid double-drawing. Clear the
        // suppression flag FIRST so the subsequent setActiveRoute
        // call from SyncActiveRouteAsync isn't no-op'd by the gate
        // we added in JS, then re-fetch and redraw from
        // Data.ActiveRouteHref.
        if (module is not null)
        {
            try { await module.InvokeVoidAsync("setActiveOverlayHidden", false); }
            catch (JSDisconnectedException) { }
        }
        if (Data.ActiveRouteHref is not null)
        {
            try { await SyncActiveRouteAsync(force: true); }
            catch (JSDisconnectedException) { }
        }
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
        routeEditOriginalName = routeEditName;  // baseline for SaveAsCopy
        chartPanelOpen = false; // Close layers panel so user can see the map.
        InstallEditNavGuard();

        // Editing the currently-active route: hide the active-route
        // overlay (the leg polyline + next-waypoint marker) during the
        // edit so it doesn't overlap the edit-mode polyline in a
        // different colour and confuse the helm. The server-side
        // course state stays active throughout -- this is a purely
        // visual suppression.
        //
        // CRITICAL: do NOT reset lastActiveRouteHref here. The href on
        // the server hasn't changed (same route id), so on the next
        // data tick SyncActiveRouteAsync's gate
        // (`currentHref == lastActiveRouteHref`) keeps the sync silent
        // and the active overlay stays cleared. Resetting it would
        // make the gate fire as "route changed" and immediately
        // redraw the overlay we just hid -- the bug fix this comment
        // protects against. CancelRouteEdit + SaveRouteCoreInner
        // restore via SyncActiveRouteAsync(force: true) which bypasses
        // the gate and re-fetches the (possibly edited) coordinates.
        if (Data.ActiveRouteHref is string activeHref
            && activeHref.EndsWith($"/{route.Id}", StringComparison.Ordinal))
        {
            // setActiveOverlayHidden(true) does the clearActiveRoute
            // AND clears the course-line + sets the JS-side suppress
            // flag so applyFrame stops redrawing the leg / bearing /
            // XTE tick on every position update. Without the flag the
            // course-line would otherwise tick along against the OLD
            // next-waypoint coordinates, pointing the helm at stale
            // geometry that the edit is in the middle of changing.
            try { await module.InvokeVoidAsync("setActiveOverlayHidden", true); }
            catch (JSDisconnectedException) { }
        }

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

    /// <summary>Flip the waypoint order in place. JS owns the array;
    /// the next stats-poll tick mirrors the new order back into
    /// routeEditCoords so the panel's numbered list re-renders.</summary>
    private async Task ReverseEditRoute()
    {
        if (module is null) return;
        try { await module.InvokeVoidAsync("reverseEditRoute"); }
        catch (JSDisconnectedException) { return; }
        await UpdateRouteStats();
    }

    // Save + optional auto-activate are the same pipeline; SaveRoute
    // just stops there, SaveRouteAndGo threads `activate:true` through
    // so the Layers-panel detour isn't needed for the single most
    // common follow-up step. SaveRouteAsCopy clears routeEditId before
    // hitting SaveRouteCore so the save path takes the POST (create)
    // branch instead of the PUT (update-in-place) branch -- the
    // current geometry lands as a NEW route on the server, leaving
    // the original untouched.
    private Task SaveRoute() => SaveRouteCore(activate: false);
    private Task SaveRouteAndGo() => SaveRouteCore(activate: true);

    private Task SaveRouteAsCopy()
    {
        // Clearing routeEditId here is intentional: SaveRouteCoreInner
        // reads it as "edit-in-place vs create-fresh". A copy is a
        // create. The "(copy)" suffix only appends when the helm
        // hasn't customised the name -- if they typed "Better Plan"
        // we keep it as-is; if the field still shows the source
        // route's name, we auto-disambiguate so the Layers list
        // doesn't end up with two entries called "Approach via X".
        if (routeEditOriginalName is not null
            && routeEditName == routeEditOriginalName
            && routeEditName.Length > 0
            && !routeEditName.EndsWith(" (copy)", StringComparison.Ordinal))
        {
            routeEditName += " (copy)";
        }
        routeEditId = null;
        return SaveRouteCore(activate: false);
    }

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
            // Local flag so the failure-toast branch below doesn't
            // need to inspect the global toast stack (which would
            // mistake an unrelated Info/Success toast for "we
            // already toasted this error" and silently swallow the
            // save failure).
            bool errorToasted = false;
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
            catch (Exception ex)
            {
                Toasts.Error($"Save route failed: {ex.Message}");
                ok = false; errMsg = ex.Message;
                errorToasted = true;
            }

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

                    // If the saved route is currently the active course,
                    // force the active-route polyline + next-waypoint
                    // marker to refetch from the new geometry. The href
                    // didn't change (same id), so the default href-diff
                    // sync would skip the refetch and the active overlay
                    // would still render the OLD coordinates -- which is
                    // exactly what the helmsman sees on Save+Go after
                    // editing the route they're already navigating.
                    if (Data.ActiveRouteHref is string href
                        && href.EndsWith($"/{existingId}", StringComparison.Ordinal))
                    {
                        try { await SyncActiveRouteAsync(force: true); }
                        catch (JSDisconnectedException) { }
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
            else if (!errorToasted)
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
            routeEditOriginalName = null;
            routeEditCoords = null;
            routeStatsTimer?.Dispose();
            routeStatsTimer = null;
            RemoveEditNavGuard();
            try { await module.InvokeVoidAsync("stopRouteEdit"); }
            catch (JSDisconnectedException) { }
            catch (JSException) { /* JS already torn down; overlay will go on next init */ }

            // If we entered edit mode while a course was active,
            // EditRoute hid the active-route overlay so it didn't
            // double-draw with the edit polyline. Clear the JS
            // suppression flag FIRST -- the subsequent setActiveRoute
            // call from SyncActiveRouteAsync would otherwise be
            // gated to a no-op. The in-place edit branch above
            // already force-syncs when the saved route IS the active
            // one; this catches SaveAsCopy / failed-save / fresh-save
            // where the original active route is still on the server
            // but its visual was suppressed for the edit session.
            if (module is not null)
            {
                try { await module.InvokeVoidAsync("setActiveOverlayHidden", false); }
                catch (JSDisconnectedException) { }
            }
            if (Data.ActiveRouteHref is not null)
            {
                try { await SyncActiveRouteAsync(force: true); }
                catch (JSDisconnectedException) { }
            }
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
