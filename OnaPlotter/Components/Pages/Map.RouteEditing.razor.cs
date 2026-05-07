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
/// Interop goes through the typed <c>IMapEditJs</c> / <c>IMapRouteJs</c>
/// wrappers; lifecycle exceptions (JSDisconnected / ObjectDisposed)
/// are absorbed inside the wrapper so call sites focus on the save /
/// edit logic. JSException stays explicit at sites where the failure
/// mode is recoverable (e.g. re-issue, toast).
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
    // whether to auto-append " (copy)" to the new route's name - if
    // the helm has already typed a different name, leave it alone;
    // only append when the field still shows the source route's name.
    private string? routeEditOriginalName;

    // --- Route Editing --------------------------------------------------

    private async Task StartRouteEdit()
    {
        // Close the Add flyout when the user picks Route - the menu
        // stayed open on entry to edit mode and then floated on top of
        // the edit panel. Matches the FabCreate* callbacks which all
        // set fabMenuOpen=false first.
        fabMenuOpen = false;
        routeEditMode = true;
        routeEditId = null;                     // fresh route, not an in-place edit
        // Prefill with the date-stamped default plus a numeric suffix
        // when the helm has already created a route with that name
        // earlier today. Prefilled (instead of placeholder) so iPad
        // helms can see the name before tapping Save and edit it in
        // place, while the keyboard-shortcut "just save" path still
        // gets a sensible name without extra typing.
        routeEditName = OnaPlotter.Utilities.UniqueRouteName.Suggest(
            $"Route {DateTime.Now:yyyyMMdd}",
            availableRoutes.Select(r => r.Name ?? string.Empty));
        routeEditStats = "0 WP / 0 nm";
        InstallEditNavGuard();
        if (_editJs is not null)
            await _editJs.StartRouteEditAsync();
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
        // not lose). Uses the native confirm() dialog - same pattern as
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
            // Quieted: 2 s instead of the default 4 s. Confirmation
            // already happened via the Discard / Keep-editing modal,
            // so this toast is just a "yep, gone" acknowledgement,
            // not a warning. Helm-feedback: longer dwell felt like
            // an apology.
            Toasts.Show($"Route edit cancelled ({wpCount} waypoints discarded)",
                ToastLevel.Info, durationSec: 2);

        routeEditMode = false;
        routeEditId = null;
        routeEditOriginalName = null;
        routeEditCoords = null;
        routeStatsTimer?.Dispose();
        routeStatsTimer = null;
        RemoveEditNavGuard();
        // Clear the persisted draft: the helm explicitly threw the
        // edit away, so the restore prompt should NOT show on next
        // load. Best-effort - a failure here just means the prompt
        // appears once and the helm taps Discard to clean it up.
        try { await RouteDraftStore.ClearAsync(); }
        catch (Exception) { }
        if (_editJs is not null)
            await _editJs.StopRouteEditAsync();

        // If we were editing the active route, EditRoute hid the
        // active-route overlay to avoid double-drawing. Clear the
        // suppression flag FIRST so the subsequent setActiveRoute
        // call from SyncActiveRouteAsync isn't no-op'd by the gate
        // we added in JS, then re-fetch and redraw from
        // Data.ActiveRouteHref.
        if (_routeJs is not null)
            await _routeJs.SetActiveOverlayHiddenAsync(false);
        if (Data.ActiveRouteHref is not null)
        {
            try
            {
                if (_activeRouteSync is not null)
                    await _activeRouteSync.SyncAsync(Data, enabledRoutes, availableRoutes, force: true);
            }
            catch (JSDisconnectedException) { }
        }
    }

    // UndoLastWaypoint() and the corresponding "Undo" button on the
    // route-edit panel were dropped on helm request: in the current
    // implementation the per-waypoint remove (× on the list rows) and
    // the marker-drag-to-reposition gestures cover the same UX, so the
    // Undo button was redundant chrome. The JS-side
    // undoLastEditWaypoint export stays for now in case a future
    // gesture (e.g. swipe-back) wants to call it - it's a few lines
    // and removing it now would force a follow-up commit if the
    // gesture lands later.

    /// <summary>App-start prompt: when a route draft survived the
    /// previous session (save failed, accidental refresh, etc.),
    /// ask the helm whether to restore it or discard. Skips
    /// silently when no draft exists or it's degenerate (less than
    /// 2 vertices). Restore enters the same edit mode the helm
    /// would reach via Add Route or the Layers-panel Edit button;
    /// Discard clears the draft and the prompt won't show again.
    /// Cancel (Esc / backdrop click) keeps the draft for next
    /// time - the helm hasn't decided yet.</summary>
    private async Task MaybeShowRouteDraftRestoreAsync()
    {
        RouteDraft? draft;
        try { draft = await RouteDraftStore.LoadAsync(); }
        catch (Exception) { return; }   // best-effort
        if (draft is null || draft.Coords.Length < 2) return;

        // Format "from N min ago" so the helm has a sense of how
        // stale the work is. Falls back to a raw timestamp if the
        // saved-at parse fails - malformed but non-empty drafts
        // are still better than nothing.
        string when = "your last session";
        if (DateTime.TryParse(draft.SavedAtIso,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out var savedAt))
        {
            var ago = DateTime.UtcNow - savedAt;
            when = ago.TotalMinutes < 1 ? "less than a minute ago"
                : ago.TotalMinutes < 60 ? $"{(int)ago.TotalMinutes} min ago"
                : ago.TotalHours < 24 ? $"{(int)ago.TotalHours} h ago"
                : $"{(int)ago.TotalDays} d ago";
        }

        string nameOrFallback = string.IsNullOrWhiteSpace(draft.Name)
            ? "(unnamed)"
            : draft.Name;
        string question = $"Restore unsaved route '{nameOrFallback}' "
            + $"({draft.Coords.Length} waypoints, {when})?";
        var choice = await Confirmations.ChooseAsync(question, ["Restore", "Discard"]);
        if (choice == "Restore")
        {
            await RestoreRouteDraftAsync(draft);
        }
        else if (choice == "Discard")
        {
            try { await RouteDraftStore.ClearAsync(); }
            catch (Exception) { }
            Toasts.Info("Route draft discarded");
        }
        // Cancel (null) -> keep draft for next time. The helm
        // hasn't decided yet.
    }

    /// <summary>Re-enter route-edit mode with the draft's coords +
    /// name + (optional) source-route id. Path mirrors
    /// <see cref="EditRoute"/> for an existing route or
    /// <see cref="StartRouteEdit"/> for a fresh one; we don't call
    /// either directly because both reset state we want to control
    /// (StartRouteEdit clobbers the name; EditRoute requires a
    /// SignalkRoute that may no longer exist server-side if the
    /// helm deleted it from another plotter mid-edit).</summary>
    private async Task RestoreRouteDraftAsync(RouteDraft draft)
    {
        if (_editJs is null)
        {
            Toasts.Error("Can't restore - map isn't ready yet.");
            return;
        }
        // If the draft references a server-side route, verify it
        // still exists. A deleted-on-another-plotter source means
        // we treat the draft as a "create new" with the saved
        // coords - losing the in-place link is the lesser evil
        // vs. crashing the save flow against a non-existent id.
        string? routeId = draft.RouteId;
        if (!string.IsNullOrEmpty(routeId)
            && !availableRoutes.Any(r => r.Id == routeId))
        {
            routeId = null;
        }

        fabMenuOpen = false;
        routeEditMode = true;
        routeEditId = routeId;
        routeEditName = draft.Name ?? "";
        routeEditOriginalName = null;
        routeEditCoords = draft.Coords;
        routeEditStats = $"{draft.Coords.Length} WP";   // refreshed by next poll tick
        InstallEditNavGuard();
        try
        {
            await _editJs.StartRouteEditAsync();
            await _editJs.LoadRouteForEditAsync(draft.Coords);
        }
        catch (JSDisconnectedException) { return; }
        catch (Microsoft.JSInterop.JSException ex)
        {
            Toasts.LogException(ex, "Restore");
            return;
        }
        routeStatsTimer = new System.Threading.Timer(
            _ => _ = UpdateRouteStats(), null,
            RouteStatsPollIntervalMs, RouteStatsPollIntervalMs);
        await UpdateRouteStats();
        Toasts.Success($"Restored route draft ({draft.Coords.Length} waypoints)");
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
        if (_editJs is null || _routeJs is null || route.Feature?.Geometry is null) return;

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
        // course state stays active throughout - this is a purely
        // visual suppression.
        //
        // CRITICAL: do NOT reset lastActiveRouteHref here. The href on
        // the server hasn't changed (same route id), so on the next
        // data tick SyncActiveRouteAsync's gate
        // (`currentHref == lastActiveRouteHref`) keeps the sync silent
        // and the active overlay stays cleared. Resetting it would
        // make the gate fire as "route changed" and immediately
        // redraw the overlay we just hid - the bug fix this comment
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
            await _routeJs.SetActiveOverlayHiddenAsync(true);
        }

        await _editJs.LoadRouteForEditAsync(coords.ToArray());
        routeStatsTimer = new System.Threading.Timer(
            _ => _ = UpdateRouteStats(), null,
            RouteStatsPollIntervalMs, RouteStatsPollIntervalMs);
        await UpdateRouteStats();
    }

    private async Task UpdateRouteStats()
    {
        if (_editJs is null) return;
        // Fetch the live edit coords; derive stats (count + total NM)
        // here in C#. Earlier code called a sibling JS getEditRouteStats
        // that ran the same haversine sum - that round-trip is gone
        // and the math now lives in one place per the project rule.
        var coords = await _editJs.GetEditRouteCoordsAsync();
        bool dirty = false;
        if (coords is not null)
        {
            int wpCount = coords.Length;
            double nm = wpCount >= 2
                ? OnaPlotter.Utilities.RouteProgress.TotalDistanceMeters(coords) / 1852.0
                : 0;
            var s = $"{wpCount} WP / {nm:F1} nm";
            if (s != routeEditStats) { routeEditStats = s; dirty = true; }
        }
        if (coords is not null && !CoordsEqual(coords, routeEditCoords))
        {
            routeEditCoords = coords;
            dirty = true;
            // Draft persistence used to fire on every coords change in
            // this poll (sub-second). That was wasteful AND surfaced a
            // restore prompt for edits the helm never tried to save.
            // Persist now happens once at the start of SaveRouteCoreInner
            // (so a failed API call leaves the work on disk) and is
            // cleared on success / cancel. Tap-and-walk-away leaves no
            // draft.
        }
        if (dirty) await InvokeAsync(StateHasChanged);
    }

    // PersistRouteDraftAsync used to live here as a per-poll-tick
    // localStorage write. Removed when the persistence trigger moved
    // to the save-attempt path (see SaveRouteCoreInner) so an edit
    // that the helm builds and abandons doesn't leave a draft for the
    // next page-load to ask about. Inlining the save into
    // SaveRouteCoreInner kept the surface area small enough that a
    // dedicated helper wasn't pulling its weight.

    private async Task RemoveRouteWaypoint(int index)
    {
        if (_editJs is null) return;
        await _editJs.RemoveRouteEditWaypointAsync(index);
        await UpdateRouteStats();
    }

    /// <summary>Flip the waypoint order in place. JS owns the array;
    /// the next stats-poll tick mirrors the new order back into
    /// routeEditCoords so the panel's numbered list re-renders.</summary>
    private async Task ReverseEditRoute()
    {
        if (_editJs is null) return;
        await _editJs.ReverseEditRouteAsync();
        await UpdateRouteStats();
    }

    // Save + optional auto-activate are the same pipeline; SaveRoute
    // just stops there, SaveRouteAndGo threads `activate:true` through
    // so the Layers-panel detour isn't needed for the single most
    // common follow-up step. SaveRouteAsCopy clears routeEditId before
    // hitting SaveRouteCore so the save path takes the POST (create)
    // branch instead of the PUT (update-in-place) branch - the
    // current geometry lands as a NEW route on the server, leaving
    // the original untouched.
    private Task SaveRoute() => SaveRouteCore(activate: false);
    private Task SaveRouteAndGo() => SaveRouteCore(activate: true);

    private Task SaveRouteAsCopy()
    {
        // Clearing routeEditId here is intentional: SaveRouteCoreInner
        // reads it as "edit-in-place vs create-fresh". A copy is a
        // create. The "(copy)" suffix only appends when the helm
        // hasn't customised the name - if they typed "Better Plan"
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
        // Stale-flag escape hatch: if the previous save threw before
        // its try/finally cleared _saveInFlight (or hung > 30 s in
        // a JS call), force-reset and proceed. Without this the Save
        // button stays silently disabled until the page is reloaded.
        if (_saveInFlight)
        {
            var heldFor = (DateTime.UtcNow - _saveInFlightStartedUtc).TotalSeconds;
            if (heldFor < SaveInFlightTimeoutSec) return;
            Console.Error.WriteLine($"[SaveRoute] _saveInFlight stuck for {heldFor:F0}s; resetting.");
        }
        _saveInFlight = true;
        _saveInFlightStartedUtc = DateTime.UtcNow;
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
        if (_editJs is null) return;

        // The wrapper swallows the disposal exceptions and returns
        // null; the JSException catch stays so a real JS bug
        // surfaces as a toast rather than freezing the save flow.
        double[][]? coords = null;
        try { coords = await _editJs.GetEditRouteCoordsAsync(); }
        catch (JSException ex) { Toasts.LogException(ex, "Read route"); }

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
                    // Warning, not Info: this is a guard rejecting the
                    // helm's save attempt, not an informational status.
                    // Severity should match the colour the helm reads
                    // ("yellow = something needs your attention").
                    Toasts.Warning("Add at least 2 waypoints before saving");
                return;
            }
            // Empty name -> date-stamped default. yyyyMMdd is monotonically
            // sortable in the routes list, which matters more than time-of-day
            // (most users create a few routes per day; "Route 14:30" starts
            // to look identical after a week). The Suggest helper appends a
            // " (N)" suffix if a route with that name already exists - self
            // is excluded by id so an in-place edit isn't disambiguated
            // against its own pre-edit name.
            string name = string.IsNullOrWhiteSpace(routeEditName)
                ? OnaPlotter.Utilities.UniqueRouteName.Suggest(
                    $"Route {DateTime.Now:yyyyMMdd}",
                    availableRoutes.Where(r => r.Id != routeEditId).Select(r => r.Name ?? string.Empty))
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

            // Persist the draft BEFORE the API call so a failed save
            // (no network, not logged in, server 5xx) leaves the
            // helm's work on disk for the next-page-load restore
            // prompt. Cleared on success below; intentionally NOT
            // cleared on failure - the whole point of the draft is
            // that it survives the failure path. Best-effort: a
            // localStorage write failure (full disk, quota) is
            // non-fatal; the helm just doesn't get the restore prompt
            // if the save then also fails, which matches the
            // pre-feature behaviour.
            try
            {
                var draft = new RouteDraft(
                    RouteId: existingId,
                    Name: name,
                    Coords: coords,
                    SavedAtIso: DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
                await RouteDraftStore.SaveAsync(draft);
            }
            catch (Exception) { /* swallow: see comment above */ }

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
                Toasts.LogException(ex, "Save route");
                ok = false; errMsg = ex.Message;
                errorToasted = true;
            }

            if (ok)
            {
                Toasts.Success($"Saved route '{name}' ({coords.Length} waypoints)");
                // Drop the persisted draft: the work is now safely
                // on the server, so the restore prompt should NOT
                // show on next load. Best-effort - a failure here
                // just means the prompt appears once and the helm
                // taps Discard.
                try { await RouteDraftStore.ClearAsync(); }
                catch (Exception) { }
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

                if (existingId is not null && newRoute is not null && _routeJs is not null)
                {
                    // Edit-in-place redraw: wipe the old polyline
                    // so the geometry change takes effect. JSException
                    // is benign here - the next addRoute replaces it.
                    try { await _routeJs.RemoveRouteAsync(existingId); }
                    catch (JSException) { /* next addRoute replaces it */ }
                    if (enabledRoutes.Contains(existingId))
                    {
                        try { await AddRouteToMap(newRoute); }
                        catch (JSException ex) { Toasts.LogException(ex, "Display route"); }
                    }

                    // If the saved route is currently the active course,
                    // force the active-route polyline + next-waypoint
                    // marker to refetch from the new geometry. The href
                    // didn't change (same id), so the default href-diff
                    // sync would skip the refetch and the active overlay
                    // would still render the OLD coordinates - which is
                    // exactly what the helmsman sees on Save+Go after
                    // editing the route they're already navigating.
                    if (Data.ActiveRouteHref is string href
                        && href.EndsWith($"/{existingId}", StringComparison.Ordinal))
                    {
                        try
                        {
                            if (_activeRouteSync is not null)
                                await _activeRouteSync.SyncAsync(Data, enabledRoutes, availableRoutes, force: true);
                        }
                        catch (JSDisconnectedException) { }
                    }
                }
                else if (existingId is null && newRoute is not null && !string.IsNullOrEmpty(newRoute.Id))
                {
                    // Fresh save: enable and draw on the map.
                    enabledRoutes.Add(newRoute.Id);
                    await Settings.SetEnabledRoutesAsync(enabledRoutes);
                    try { await AddRouteToMap(newRoute); }
                    catch (JSException ex) { Toasts.LogException(ex, "Display route"); }
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
            // JSException here is benign: the JS side is already torn
            // down (the overlay rebuilds on the next init).
            try { if (_editJs is not null) await _editJs.StopRouteEditAsync(); }
            catch (JSException) { /* JS already torn down; overlay will go on next init */ }

            // If we entered edit mode while a course was active,
            // EditRoute hid the active-route overlay so it didn't
            // double-draw with the edit polyline. Clear the JS
            // suppression flag FIRST - the subsequent setActiveRoute
            // call from SyncActiveRouteAsync would otherwise be
            // gated to a no-op. The in-place edit branch above
            // already force-syncs when the saved route IS the active
            // one; this catches SaveAsCopy / failed-save / fresh-save
            // where the original active route is still on the server
            // but its visual was suppressed for the edit session.
            if (_routeJs is not null)
                await _routeJs.SetActiveOverlayHiddenAsync(false);
            if (Data.ActiveRouteHref is not null)
            {
                try
                {
                    if (_activeRouteSync is not null)
                        await _activeRouteSync.SyncAsync(Data, enabledRoutes, availableRoutes, force: true);
                }
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
                Toasts.LogException(ex, "Route saved, but start navigation");
            }
        }
    }
}
