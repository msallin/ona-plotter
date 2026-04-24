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
    // popup surfaces Activate + Delete without making the user open the
    // Layers panel -- the common "pick this route" workflow was a half-
    // dozen taps via the panel, now it's one tap on the line itself.
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
        if (Data.HasActiveCourse && !string.IsNullOrEmpty(Data.ActiveRouteHref)
            && !Data.ActiveRouteHref.Contains(id, StringComparison.Ordinal))
        {
            var prompt = !string.IsNullOrEmpty(Data.ActiveRouteName)
                ? $"Replace active course '{Data.ActiveRouteName}' with '{route.Name ?? route.Id}'?"
                : $"Replace the active course with '{route.Name ?? route.Id}'?";
            bool ok = await Confirmations.ConfirmAsync(prompt);
            if (!ok) return;
        }

        await NavigateRouteInternal(route);
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

    // Thin wrapper around the private NavigateRoute logic so JSInvokable
    // activation can reuse it without duplicating the try/catch.
    //
    // Also enforces route/anchor mutual exclusion: the two HUDs share
    // the bottom-center slot and represent incompatible intents. If the
    // user engages a route while anchored, drop the anchor first so
    // depth / anchor-drag alarms don't fire against a moving boat.
    private async Task NavigateRouteInternal(SignalkRoute route)
    {
        // Real-world workflow: the boat is on the hook, the helm plans
        // the passage, lifts anchor, goes. Treating "activate route" as
        // an implicit "lift anchor" command matches that flow -- the
        // user already confirmed they want to leave by activating a
        // route, so a second modal to confirm lifting the anchor is
        // cockpit-theatre. The earlier "block and tell them to tap
        // anchor first" behaviour was defensive but wrong for the
        // sequence sailors actually follow.
        //
        // Order matters: raise BEFORE setting the course. If setting
        // the course failed for some other reason we'd be left
        // anchor-up with no active route, which is a minor re-plan
        // nuisance; the opposite (still anchored while a course was
        // set and CPA / APPROACH alarms start chattering against a
        // static boat) is the state we just fought with an explicit
        // rule in the alarm sweep.
        if (Data.AnchorActive)
        {
            try
            {
                var r = await AnchorAlarmApi.RaiseAsync();
                if (!r.Success)
                {
                    // Plugin rejected (wrong state, not installed, etc.).
                    // Fall back to the old prompt-the-helm behaviour so
                    // the user still has a way out.
                    Toasts.Error($"Couldn't auto-raise anchor ({r.Error ?? "server rejected"}). Raise manually, then retry.");
                    return;
                }
                Toasts.Info("Anchor raised for route");
            }
            catch (Exception ex)
            {
                Toasts.Error($"Couldn't auto-raise anchor ({ex.Message}). Raise manually, then retry.");
                return;
            }
        }
        if (anchorManualActive && module is not null)
        {
            try { await module.InvokeVoidAsync("clearAnchor"); anchorManualActive = false; }
            catch (JSDisconnectedException) { }
        }

        try
        {
            var r = await CourseApi.SetActiveRouteAsync(route.Id);
            if (r.Success)
            {
                Toasts.Success($"Navigating route '{route.Name ?? route.Id}'");
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
