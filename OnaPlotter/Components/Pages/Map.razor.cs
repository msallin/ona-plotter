using Microsoft.JSInterop;
using OnaPlotter.Models;
using OnaPlotter.Services;
using OnaPlotter.Services.Api;
using OnaPlotter.Utilities;

namespace OnaPlotter.Components.Pages;

/// <summary>
/// Code-behind for the Map page. Holds the CRUD flow for the three
/// server-stored resources the user can create from the chart --
/// waypoints, notes, regions. These share a shape (context-menu
/// entry -> modal dialog -> POST -> JS marker draw) and moving them
/// out of the .razor keeps the markup file focused on layout and
/// core map state.
///
/// Razor source-generates the Map class from Map.razor; this partial
/// composes into the same class. Fields declared here are visible
/// from the markup and the razor-level @code section, and vice versa.
/// </summary>
public partial class Map
{
    // ---- Waypoint (create + save) ------------------------------------
    private bool waypointDialogVisible;
    private string newWaypointName = "";
    private string newWaypointDescription = "";
    private List<SignalkWaypoint> loadedWaypoints = [];

    private void CreateWaypointHere()
    {
        contextMenuVisible = false;
        waypointDialogVisible = true;
        newWaypointName = "";
        newWaypointDescription = "";
    }

    private async Task SaveWaypoint()
    {
        waypointDialogVisible = false;
        string name = string.IsNullOrWhiteSpace(newWaypointName) ? $"WPT {DateTime.Now:HH:mm}" : newWaypointName;
        string? description = string.IsNullOrWhiteSpace(newWaypointDescription) ? null : newWaypointDescription.Trim();
        ApiResult<string> r;
        try { r = await WaypointApi.CreateAsync(name, contextMenuLat, contextMenuLon, description); }
        catch (Exception ex) { Toasts.Error($"Save waypoint failed: {ex.Message}"); return; }

        if (!r.Success || string.IsNullOrEmpty(r.Value))
        {
            Toasts.Error($"Save waypoint failed: {r.Error ?? "server rejected"}");
            return;
        }
        string id = r.Value;

        if (module is not null)
            await module.InvokeVoidAsync("addWaypointMarker", id, contextMenuLat, contextMenuLon, name);
        loadedWaypoints = await SafeLoad(() => WaypointApi.GetAllAsync(), "waypoints") ?? loadedWaypoints;
        Toasts.Success($"Saved waypoint '{name}'");
    }

    /// <summary>Invoked from the waypoint popup's Delete button.
    /// Round-trips DELETE to the server, removes the marker, and drops
    /// the waypoint from the loaded list. Parallels <see cref="DeleteNote"/>
    /// / <see cref="DeleteRegion"/> for consistency.</summary>
    [JSInvokable]
    public async Task DeleteWaypoint(string id)
    {
        ApiResult r;
        try { r = await WaypointApi.DeleteAsync(id); }
        catch (Exception ex) { Toasts.Error($"Delete waypoint failed: {ex.Message}"); return; }
        if (!r.Success) { Toasts.Error($"Delete waypoint failed: {r.Error ?? "server rejected"}"); return; }

        if (module is not null)
            await module.InvokeVoidAsync("removeWaypointMarker", id);
        loadedWaypoints = loadedWaypoints.Where(w => w.Id != id).ToList();
        Toasts.Info("Waypoint deleted");
    }

    // ---- Note (create, save, delete, focus, show/hide) ---------------
    private bool noteDialogVisible;
    private string newNoteTitle = "";
    private string newNoteDescription = "";
    private List<SignalkNote> loadedNotes = [];
    private bool notesVisible = true;

    private void CreateNoteHere()
    {
        contextMenuVisible = false;
        noteDialogVisible = true;
        newNoteTitle = "";
        newNoteDescription = "";
    }

    private async Task SaveNote()
    {
        noteDialogVisible = false;
        string title = string.IsNullOrWhiteSpace(newNoteTitle) ? $"Note {DateTime.Now:HH:mm}" : newNoteTitle;
        string description = newNoteDescription ?? "";
        ApiResult<string> r;
        try { r = await NoteApi.CreateAsync(title, description, contextMenuLat, contextMenuLon); }
        catch (Exception ex) { Toasts.Error($"Save note failed: {ex.Message}"); return; }

        if (!r.Success || string.IsNullOrEmpty(r.Value))
        {
            Toasts.Error($"Save note failed: {r.Error ?? "server rejected"}");
            return;
        }
        string id = r.Value;

        if (module is not null)
            await module.InvokeVoidAsync("addNoteMarker", id, contextMenuLat, contextMenuLon, title, description);
        loadedNotes = await SafeLoad(() => NoteApi.GetAllAsync(), "notes") ?? loadedNotes;
        Toasts.Success($"Saved note '{title}'");
    }

    /// <summary>Invoked from the JS popup's Delete button. Round-trips
    /// DELETE to the server, then removes the marker. Called via
    /// <c>dotNetRef.invokeMethodAsync('DeleteNote', id)</c>.</summary>
    [JSInvokable]
    public async Task DeleteNote(string id)
    {
        ApiResult r;
        try { r = await NoteApi.DeleteAsync(id); }
        catch (Exception ex) { Toasts.Error($"Delete note failed: {ex.Message}"); return; }
        if (!r.Success) { Toasts.Error($"Delete note failed: {r.Error ?? "server rejected"}"); return; }

        if (module is not null)
            await module.InvokeVoidAsync("removeNoteMarker", id);
        loadedNotes = loadedNotes.Where(n => n.Id != id).ToList();
        Toasts.Info("Note deleted");
    }

    private async Task FocusNote(SignalkNote note)
    {
        if (module is null || note.Position is null) return;
        chartPanelOpen = false;
        // Un-hide first so the pin's popup actually has something to
        // open -- hidden notes have no marker instance in the JS side
        // and openNotePopup would silently no-op.
        if (!notesVisible)
        {
            await ToggleNotesVisible(true);
        }
        await DisableFollowAsync();
        try
        {
            await module.InvokeVoidAsync("panTo", note.Position.Latitude, note.Position.Longitude);
            await module.InvokeVoidAsync("openNotePopup", note.Id);
        }
        catch (JSDisconnectedException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task ToggleNotesVisible(bool visible)
    {
        notesVisible = visible;
        if (module is null) return;
        try
        {
            if (visible)
            {
                foreach (var n in loadedNotes)
                {
                    if (n.Position is null) continue;
                    await module.InvokeVoidAsync("addNoteMarker",
                        n.Id, n.Position.Latitude, n.Position.Longitude, n.Title, n.Description);
                }
            }
            else
            {
                await module.InvokeVoidAsync("clearNotes");
            }
        }
        catch (JSDisconnectedException) { }
        catch (ObjectDisposedException) { }
    }

    // ---- Region (create, save, delete, focus, show/hide) -------------
    private bool regionDialogVisible;
    // "circle" -- quick radius preset; "polygon" -- freeform draw. Mode
    // toggle lives inside the Add Region dialog; we keep the last choice
    // as the default for next time so a user who mostly draws polygons
    // doesn't re-pick every time.
    private string newRegionMode = "circle";
    private string newRegionTitle = "";
    private string newRegionDescription = "";
    private double newRegionRadiusMeters = 250;
    private List<SignalkRegion> loadedRegions = [];
    private bool regionsVisible = true;

    // Radius presets for circle-region creation. Covers typical
    // anchorage / watch-area / no-go scales without a free-form input
    // that would require validation. 1 nm = 1852 m.
    private static readonly (double Meters, string Label)[] RegionRadiusPresets =
    [
        (100, "100 m"),
        (250, "250 m"),
        (500, "500 m"),
        (1_852, "1 nm"),
        (3_704, "2 nm"),
    ];

    private async Task CreateRegionHere()
    {
        contextMenuVisible = false;
        regionDialogVisible = true;
        newRegionTitle = "";
        newRegionDescription = "";
        newRegionRadiusMeters = 250;
        // Default mode is the last pick (newRegionMode persists within the
        // session); draw the preview immediately if circle is the choice
        // so the user sees the real size before touching a radius chip.
        if (newRegionMode == "circle") await UpdateCirclePreview();
    }

    // Mode switch from the dialog toggle. Clears the preview when the
    // user flips to Polygon because the polygon flow doesn't use it.
    private async Task SetRegionMode(string mode)
    {
        newRegionMode = mode;
        if (mode == "circle") await UpdateCirclePreview();
        else await ClearCirclePreview();
    }

    // Radius chip click. Persist the pick via newRegionRadiusMeters and
    // refresh the preview so the user can tune the size visually.
    private async Task PickRegionRadius(double meters)
    {
        newRegionRadiusMeters = meters;
        await UpdateCirclePreview();
    }

    private async Task UpdateCirclePreview()
    {
        if (module is null) return;
        try
        {
            await module.InvokeVoidAsync("setCirclePreview",
                contextMenuLat, contextMenuLon, newRegionRadiusMeters);
        }
        catch (JSDisconnectedException) { }
    }

    private async Task ClearCirclePreview()
    {
        if (module is null) return;
        try { await module.InvokeVoidAsync("clearCirclePreview"); }
        catch (JSDisconnectedException) { }
    }

    // Cancel button path. Close the dialog AND clear the preview so we
    // don't leave a ghost circle floating over the map.
    private async Task CancelRegionDialog()
    {
        regionDialogVisible = false;
        await ClearCirclePreview();
    }

    private async Task SaveRegion()
    {
        regionDialogVisible = false;
        await ClearCirclePreview();
        string title = string.IsNullOrWhiteSpace(newRegionTitle)
            ? $"Region {DateTime.Now:HH:mm}"
            : newRegionTitle;
        string description = newRegionDescription ?? "";
        ApiResult<string> r;
        try
        {
            r = await RegionApi.CreateCircleAsync(title, description,
                contextMenuLat, contextMenuLon, newRegionRadiusMeters);
        }
        catch (Exception ex) { Toasts.Error($"Save region failed: {ex.Message}"); return; }
        if (!r.Success || string.IsNullOrEmpty(r.Value))
        {
            Toasts.Error($"Save region failed: {r.Error ?? "server rejected"}");
            return;
        }
        string id = r.Value;

        // Re-fetch so the new region has the same shape the server sent
        // back (id-from-key, Leaflet-ordered rings). Simpler than
        // locally-replicating the circle-polygon math for the optimistic
        // insert.
        loadedRegions = await SafeLoad(() => RegionApi.GetAllAsync(), "regions") ?? loadedRegions;
        var created = loadedRegions.FirstOrDefault(rg => rg.Id == id);
        if (created is not null && module is not null)
            await module.InvokeVoidAsync("addRegion",
                created.Id, created.OuterRings, created.Name, created.Description);
        Toasts.Success($"Saved region '{title}'");
    }

    [JSInvokable]
    public async Task DeleteRegion(string id)
    {
        ApiResult r;
        try { r = await RegionApi.DeleteAsync(id); }
        catch (Exception ex) { Toasts.Error($"Delete region failed: {ex.Message}"); return; }
        if (!r.Success) { Toasts.Error($"Delete region failed: {r.Error ?? "server rejected"}"); return; }

        if (module is not null)
            await module.InvokeVoidAsync("removeRegion", id);
        loadedRegions = loadedRegions.Where(rg => rg.Id != id).ToList();
        Toasts.Info("Region deleted");
    }

    // --- Route popup actions -------------------------------------------
    //
    // Activated from the tap-to-popup on a saved route polyline. The
    // popup surfaces Activate + Delete in a single tap on the line so
    // the helm doesn't have to open the Layers panel and walk through
    // half a dozen taps for the common "pick this route" flow.
    //
    // Matching JS calls:
    //   dotNetRef.invokeMethodAsync('ActivateRouteById', id)
    //   dotNetRef.invokeMethodAsync('DeleteRouteById', id)

    [JSInvokable]
    public async Task ActivateRouteById(string id)
    {
        var route = availableRoutes.FirstOrDefault(r => r.Id == id);
        if (route is null) { Toasts.Error("Route not found"); return; }

        // If a DIFFERENT route is already the active course, don't
        // silently switch -- one stray tap on the route-popup Activate
        // button would drop whatever navigation is running. Prompt so
        // the intent is explicit. No prompt when:
        //   - nothing is active (first activation)
        //   - this exact route is already active (re-activating is a no-op)
        // EndsWith on `/{id}` rather than Contains: a substring match
        // could mis-classify an unrelated route whose id happens to
        // sit inside the active href as "already active" and skip the
        // prompt.
        if (Data.HasActiveCourse && !string.IsNullOrEmpty(Data.ActiveRouteHref)
            && !Data.ActiveRouteHref.EndsWith($"/{id}", StringComparison.Ordinal))
        {
            var prompt = !string.IsNullOrEmpty(Data.ActiveRouteName)
                ? $"Replace active course '{Data.ActiveRouteName}' with '{route.Name ?? route.Id}'?"
                : $"Replace the active course with '{route.Name ?? route.Id}'?";
            bool ok = await Confirmations.ConfirmAsync(prompt);
            if (!ok) return;
        }

        await NavigateRouteInternal(route);
    }

    /// <summary>
    /// Jumps the active route to a specific 0-based leg index. Wired to a
    /// click on a non-active waypoint dot in the route polyline (the
    /// pulsing next-WP marker is skipped since it's already the target).
    /// Mirrors Freeboard-SK's "tap a WP to make it the next leg" gesture.
    /// </summary>
    /// <remarks>
    /// Defence-in-depth: the JS side passes the route id it drew with,
    /// but the user could tap a stale dot a moment after a course change.
    /// Verify the id still matches the current active course before
    /// PUTting, otherwise we'd risk silently re-activating an old route.
    /// Confirmation is on -- accidental taps mid-passage shouldn't
    /// re-route the boat.
    /// </remarks>
    [JSInvokable]
    public async Task JumpToRouteWaypoint(string routeId, int pointIndex)
    {
        if (string.IsNullOrEmpty(routeId)) return;
        var activeHref = Data.ActiveRouteHref;
        if (string.IsNullOrEmpty(activeHref)) return;
        if (!string.Equals(SignalKUrls.ExtractRouteId(activeHref), routeId, StringComparison.Ordinal))
            return;
        if (pointIndex < 0) return;
        if (Data.ActiveRoutePointIndex == pointIndex) return; // already there

        int wpNumber = pointIndex + 1;
        string routeLabel = Data.ActiveRouteName is { Length: > 0 } n ? $"'{n}'" : "the active route";
        bool ok = await Confirmations.ConfirmAsync(
            $"Skip to waypoint {wpNumber} of {routeLabel}?");
        if (!ok) return;

        try
        {
            var r = await CourseApi.SetPointIndexAsync(pointIndex);
            if (!r.Success)
            {
                Toasts.Error($"Skip to WP {wpNumber} failed: {r.Error ?? "server rejected"}");
                return;
            }
            Toasts.Success($"Skipped to WP {wpNumber}");
        }
        catch (Exception ex) { Toasts.Error($"Skip to WP {wpNumber} failed: {ex.Message}"); }
    }

    [JSInvokable]
    public async Task DeleteRouteById(string id)
    {
        ApiResult r;
        try { r = await RouteApi.DeleteAsync(id); }
        catch (Exception ex) { Toasts.Error($"Delete route failed: {ex.Message}"); return; }
        if (!r.Success) { Toasts.Error($"Delete route failed: {r.Error ?? "server rejected"}"); return; }

        // Strip from enabled + draw order so the UI forgets it too.
        if (enabledRoutes.Remove(id))
            await Settings.SetEnabledRoutesAsync(enabledRoutes);

        if (module is not null)
            try { await module.InvokeVoidAsync("removeRoute", id); }
            catch (JSDisconnectedException) { }

        availableRoutes = availableRoutes.Where(rt => rt.Id != id).ToList();
        Toasts.Info("Route deleted");
        RebuildFilteredLayers();
    }

    /// <summary>
    /// Stops the currently-active SignalK course. Called from the
    /// "Deactivate" button on the active-route popup (tap the polyline
    /// while a route is active). Mirrors the bottom-bar Stop Navigation
    /// button so the helm has two paths to the same action -- the bar
    /// for "fast access while overlooking the chart", the popup for
    /// "I'm already pointing at the route I want to dismiss".
    /// </summary>
    [JSInvokable]
    public Task DeactivateActiveRoute() => StopNavigation();

    // Thin wrapper around the private NavigateRoute logic so JSInvokable
    // activation can reuse it without duplicating the try/catch.
    //
    // Note on anchor / route relationship: route activation NO LONGER
    // auto-raises the anchor. The reverse direction stays (dropping the
    // anchor clears any active course -- see SyncServerAnchorAsync /
    // ToggleAnchor) because anchoring is the more decisive intent: a
    // boat that just dropped the hook is unambiguously not under way.
    // Going the other direction (activating a route) does NOT mean the
    // helm has actually lifted the anchor yet -- they may be planning
    // the next leg while still on the hook. Auto-raising on activate
    // was wrong for that workflow and surprised users when they
    // weren't ready to leave.
    private async Task NavigateRouteInternal(SignalkRoute route)
    {
        try
        {
            var r = await CourseApi.SetActiveRouteAsync(route.Id);
            if (r.Success)
            {
                Toasts.Success($"Navigating route '{route.Name ?? route.Id}'");

                // Reminder, not an action: if the helm activates a
                // route while still anchored, the boat will start
                // moving but the server's anchor watch is still armed.
                // The drag alarm rule fires the moment the boat leaves
                // the anchor radius, blasting ANCHOR DRAG repeatedly.
                // We deliberately don't auto-raise (per user spec --
                // they may be planning the next leg from the hook),
                // but we DO surface a non-blocking note so the helm
                // remembers to raise before getting under way.
                if (Data.AnchorActive || anchorManualActive)
                {
                    Toasts.Show("Anchor still active -- raise it before getting under way to silence the drag alarm",
                        ToastService.ToastLevel.Info, durationSec: 8);
                }
                // Force an immediate route draw instead of waiting for
                // the next delta tick to notice the href change. The
                // href-diff lives in SyncActiveRouteAsync; just wipe
                // lastActiveRouteHref so the next tick is guaranteed
                // to refetch, and kick the sync synchronously so the
                // user sees the polyline appear right away.
                lastActiveRouteHref = null;
                await SyncActiveRouteAsync();
            }
            else Toasts.Error($"Start route failed: {r.Error ?? "server rejected"}");
        }
        catch (Exception ex) { Toasts.Error($"Start route failed: {ex.Message}"); }
    }

    private async Task FocusRegion(SignalkRegion region)
    {
        if (module is null || region.OuterRings.Count == 0) return;
        chartPanelOpen = false;
        // If the user hid regions via the Show toggle, clicking Focus
        // on a region row should still land them at it. Un-hide first
        // so the polygon is actually on the map when the bounds-fit
        // runs; otherwise Focus is a silent no-op which is confusing.
        if (!regionsVisible)
        {
            await ToggleRegionsVisible(true);
        }
        await DisableFollowAsync();
        try
        {
            await module.InvokeVoidAsync("focusRegion", region.Id, region.OuterRings[0]);
        }
        catch (JSDisconnectedException) { }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Turns off follow-boat mode when the user explicitly pans to
    /// something on the map (vessel, note, region, waypoint). Without
    /// this the next position update re-centres on own boat and the
    /// user sees a flash of the target, then a snap back -- the
    /// field-reported "goes to wrong location" bug. Shared helper so
    /// every Focus* path uses the same logic.
    /// </summary>
    private async Task DisableFollowAsync()
    {
        if (!follow) return;
        follow = false;
        await Settings.SetFollowBoatAsync(false);
        if (module is not null)
            try { await module.InvokeVoidAsync("setFollow", false); }
            catch (JSDisconnectedException) { }
    }

    private async Task ToggleRegionsVisible(bool visible)
    {
        regionsVisible = visible;
        if (module is null) return;
        try
        {
            if (visible)
            {
                foreach (var r in loadedRegions)
                    await module.InvokeVoidAsync("addRegion",
                        r.Id, r.OuterRings, r.Name, r.Description);
            }
            else
            {
                await module.InvokeVoidAsync("clearRegions");
            }
        }
        catch (JSDisconnectedException) { }
        catch (ObjectDisposedException) { }
    }

}
