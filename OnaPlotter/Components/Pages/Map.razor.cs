using Microsoft.JSInterop;
using OnaPlotter.Models;
using OnaPlotter.Services;
using OnaPlotter.Services.Api;
using OnaPlotter.Services.Json;
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
    /// <summary>Non-null = the dialog is in EDIT mode for this
    /// waypoint id; null = CREATE mode at <c>contextMenuLat/Lon</c>.
    /// Drives both the dialog header text ("Edit Waypoint" vs
    /// "Create Waypoint") and the SaveWaypoint branch
    /// (UpdateAsync vs CreateAsync). Reset to null on every dialog
    /// close (Save success, Save failure, Cancel) so the next Create
    /// flow doesn't accidentally update the previously-edited
    /// waypoint.</summary>
    private string? waypointEditId;
    private List<SignalkWaypoint> loadedWaypoints = [];

    private void CreateWaypointHere()
    {
        contextMenuVisible = false;
        waypointEditId = null;
        waypointDialogVisible = true;
        newWaypointName = "";
        newWaypointDescription = "";
    }

    /// <summary>Open the waypoint dialog pre-filled for an in-place
    /// rename + description edit. Same dialog as Create so the helm
    /// gets one consistent layout for both flows; the only difference
    /// is the header text and which API verb the Save button hits.
    /// </summary>
    private void OpenWaypointEditDialog(SignalkWaypoint wp)
    {
        waypointEditId = wp.Id;
        newWaypointName = wp.Name ?? "";
        newWaypointDescription = wp.Description ?? "";
        waypointDialogVisible = true;
    }

    private async Task SaveWaypoint()
    {
        waypointDialogVisible = false;
        string? editingId = waypointEditId;
        // Reset the edit-id BEFORE awaiting any API call so a
        // concurrent Create flow (helm tap-and-tap-elsewhere) doesn't
        // race with the in-flight save and reuse the wrong id.
        waypointEditId = null;

        string name = string.IsNullOrWhiteSpace(newWaypointName) ? $"WPT {DateTime.Now:HH:mm}" : newWaypointName;
        string? description = string.IsNullOrWhiteSpace(newWaypointDescription) ? null : newWaypointDescription.Trim();

        if (editingId is not null)
        {
            // Edit branch: PUT in place. The server keeps the same id,
            // we update the local list optimistically, and the marker
            // re-renders with the new text on the next popup open.
            var existing = loadedWaypoints.FirstOrDefault(w => w.Id == editingId);
            if (existing is null) { Toasts.Warning("Waypoint not found"); return; }

            ApiResult ru;
            try { ru = await WaypointApi.UpdateAsync(existing, name, description); }
            catch (Exception ex) { Toasts.LogException(ex, $"Save waypoint '{name}'"); return; }
            if (!ru.Success) { Toasts.Error($"Save waypoint '{name}' failed: {ru.Error ?? "server rejected"}"); return; }

            loadedWaypoints = loadedWaypoints.Select(w =>
            {
                if (w.Id != editingId) return w;
                return new SignalkWaypoint
                {
                    Id = w.Id,
                    Name = name,
                    Description = description,
                    Feature = w.Feature,
                    Latitude = w.Latitude,
                    Longitude = w.Longitude,
                    CreatedAt = w.CreatedAt,
                };
            }).ToList();
            if (module is not null && existing.Latitude is double elat && existing.Longitude is double elon)
            {
                await module.InvokeVoidAsync("removeWaypointMarker", editingId);
                await module.InvokeVoidAsync("addWaypointMarker",
                    editingId, elat, elon, name,
                    existing.CreatedAt?.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
            }
            Toasts.Success($"Saved waypoint '{name}'");
            return;
        }

        // Create branch: POST at the context-menu position.
        ApiResult<string> r;
        try { r = await WaypointApi.CreateAsync(name, contextMenuLat, contextMenuLon, description); }
        catch (Exception ex) { Toasts.LogException(ex, $"Save waypoint '{name}'"); return; }

        if (!r.Success || string.IsNullOrEmpty(r.Value))
        {
            Toasts.Error($"Save waypoint '{name}' failed: {r.Error ?? "server rejected"}");
            return;
        }
        string id = r.Value;

        if (module is not null)
        {
            // Stamp createdAt with the same instant we just sent on
            // the POST so the marker popup shows the right value
            // immediately. The server's round-trip on next reload
            // will agree (resources-fs preserves the body verbatim).
            string createdAtIso = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
            await module.InvokeVoidAsync("addWaypointMarker",
                id, contextMenuLat, contextMenuLon, name, createdAtIso);
        }
        loadedWaypoints = await SafeLoad(() => WaypointApi.GetAllAsync(), "waypoints") ?? loadedWaypoints;
        Toasts.Success($"Saved waypoint '{name}'");
    }

    private void CancelWaypointDialog()
    {
        waypointDialogVisible = false;
        waypointEditId = null;
    }

    /// <summary>Invoked from the waypoint popup's Delete button.
    /// Round-trips DELETE to the server, removes the marker, and drops
    /// the waypoint from the loaded list. Parallels <see cref="DeleteNote"/>
    /// / <see cref="DeleteRegion"/> for consistency.</summary>
    [JSInvokable]
    public async Task DeleteWaypoint(string id)
    {
        // Resolve a helm-readable label up front so the failure toasts
        // can name the waypoint that didn't delete; helm bulk-deleting
        // shouldn't have to guess which one threw.
        string label = loadedWaypoints.FirstOrDefault(w => w.Id == id)?.Name
            ?? id;

        ApiResult r;
        try { r = await WaypointApi.DeleteAsync(id); }
        catch (Exception ex) { Toasts.LogException(ex, $"Delete waypoint '{label}'"); return; }
        if (!r.Success) { Toasts.Error($"Delete waypoint '{label}' failed: {r.Error ?? "server rejected"}"); return; }

        if (module is not null)
            await module.InvokeVoidAsync("removeWaypointMarker", id);
        loadedWaypoints.RemoveAll(w => w.Id == id);
        Toasts.Info($"Waypoint '{label}' deleted");
    }

    /// <summary>Popup-side Focus: pan the map to the waypoint and
    /// dismiss other panels. Mirror of <see cref="FocusWaypoint"/>
    /// (the layers-panel button), exposed as a JSInvokable so the
    /// popup-side action can call it directly without round-tripping
    /// through the parent page's EventCallback chain.</summary>
    [JSInvokable]
    public async Task WaypointFocus(string id)
    {
        var wp = loadedWaypoints.FirstOrDefault(w => w.Id == id);
        if (wp is null) { Toasts.Warning("Waypoint not found"); return; }
        await FocusWaypoint(wp);
    }

    /// <summary>Popup-side Go: PUT the waypoint as the SignalK
    /// course destination. Mirror of <see cref="NavigateToWaypoint"/>
    /// so the affordance is identical from the layers panel and the
    /// popup.</summary>
    [JSInvokable]
    public async Task WaypointGoTo(string id)
    {
        var wp = loadedWaypoints.FirstOrDefault(w => w.Id == id);
        if (wp is null) { Toasts.Warning("Waypoint not found"); return; }
        await NavigateToWaypoint(wp);
    }

    /// <summary>Popup-side Edit: open the same dialog used for
    /// Create, pre-filled with the waypoint's current name +
    /// description. Helm field-feedback was that a single-field
    /// PromptAsync rename hid the description and forced two trips
    /// (one to rename, a separate flow to edit description); the
    /// reused dialog gives a single edit surface.</summary>
    [JSInvokable]
    public Task WaypointEdit(string id)
    {
        var wp = loadedWaypoints.FirstOrDefault(w => w.Id == id);
        if (wp is null) { Toasts.Warning("Waypoint not found"); return Task.CompletedTask; }
        OpenWaypointEditDialog(wp);
        StateHasChanged();
        return Task.CompletedTask;
    }

    /// <summary>Popup-side Share: build a GeoJSON Feature for the
    /// waypoint (point + name / createdAt properties) and hand it to
    /// <see cref="ShareService"/> which owns the file-transfer +
    /// toast surface shared with the Note and MOB share paths.</summary>
    [JSInvokable]
    public async Task WaypointShare(string id)
    {
        var wp = loadedWaypoints.FirstOrDefault(w => w.Id == id);
        if (wp?.Latitude is null || wp.Longitude is null)
        {
            Toasts.Warning("Waypoint not found"); return;
        }
        var feature = new GeoJsonShareWaypointFeature(
            "Feature",
            new GeoJsonPointGeometry("Point", [wp.Longitude.Value, wp.Latitude.Value]),
            new GeoJsonShareWaypointProperties(
                Name: wp.Name,
                CreatedAt: wp.CreatedAt?.ToString("o", System.Globalization.CultureInfo.InvariantCulture)));
        string json = System.Text.Json.JsonSerializer.Serialize(
            feature, OnaGeoJsonContext.Default.GeoJsonShareWaypointFeature);
        string title = string.IsNullOrWhiteSpace(wp.Name) ? "Waypoint" : wp.Name!;
        await ShareService.ShareJsonAsync(title, json);
    }

    /// <summary>MOB popup-side Share: serialise the casualty fix as
    /// a one-off waypoint feature (same shape WaypointShare uses)
    /// and route through <see cref="ShareService"/>. The helm reading
    /// the MOB position into the VHF mic gets a one-tap "now share
    /// the same coords with the rescue coordinator on WhatsApp /
    /// SMS / mail" affordance. createdAtIso plumbs through so a
    /// shared casualty note carries the same timestamp every plotter
    /// in the area shows.</summary>
    [JSInvokable]
    public async Task MobShare(double lat, double lon, string? createdAtIso)
    {
        var feature = new GeoJsonShareWaypointFeature(
            "Feature",
            new GeoJsonPointGeometry("Point", [lon, lat]),
            new GeoJsonShareWaypointProperties(
                Name: "MOB",
                CreatedAt: createdAtIso));
        string json = System.Text.Json.JsonSerializer.Serialize(
            feature, OnaGeoJsonContext.Default.GeoJsonShareWaypointFeature);
        await ShareService.ShareJsonAsync("MOB", json, errorContext: "MOB share");
    }

    // ---- Note (create, save, delete, focus, show/hide) ---------------
    private bool noteDialogVisible;
    private string newNoteTitle = "";
    private string newNoteDescription = "";
    /// <summary>Edit-mode marker for the note dialog. Same role as
    /// <see cref="waypointEditId"/>: non-null = PUT in place,
    /// null = POST at <c>contextMenuLat/Lon</c>. Cleared on every
    /// dialog close so a Cancel followed by a Create doesn't reuse
    /// the previous edit's id.</summary>
    private string? noteEditId;
    private List<SignalkNote> loadedNotes = [];
    private bool notesVisible = true;

    private void CreateNoteHere()
    {
        contextMenuVisible = false;
        noteEditId = null;
        noteDialogVisible = true;
        newNoteTitle = "";
        newNoteDescription = "";
    }

    /// <summary>Open the note dialog pre-filled for an in-place edit.
    /// Same dialog as Add Note so the helm sees one consistent layout
    /// for both flows; the only difference is the header text and
    /// the API verb the Save button hits.</summary>
    private void OpenNoteEditDialog(SignalkNote n)
    {
        noteEditId = n.Id;
        newNoteTitle = n.Title ?? "";
        newNoteDescription = n.Description ?? "";
        noteDialogVisible = true;
    }

    private async Task SaveNote()
    {
        noteDialogVisible = false;
        string? editingId = noteEditId;
        // Reset BEFORE awaiting (see SaveWaypoint for the rationale).
        noteEditId = null;

        string title = string.IsNullOrWhiteSpace(newNoteTitle) ? $"Note {DateTime.Now:HH:mm}" : newNoteTitle;
        string description = newNoteDescription ?? "";

        if (editingId is not null)
        {
            // Edit branch: PUT in place.
            var existing = loadedNotes.FirstOrDefault(n => n.Id == editingId);
            if (existing is null) { Toasts.Warning("Note not found"); return; }

            ApiResult ru;
            try { ru = await NoteApi.UpdateAsync(existing, title, description); }
            catch (Exception ex) { Toasts.LogException(ex, $"Save note '{title}'"); return; }
            if (!ru.Success) { Toasts.Error($"Save note '{title}' failed: {ru.Error ?? "server rejected"}"); return; }

            loadedNotes = loadedNotes.Select(x =>
            {
                if (x.Id != editingId) return x;
                return new SignalkNote
                {
                    Id = x.Id,
                    Title = title,
                    Description = description,
                    Position = x.Position,
                    MimeType = x.MimeType,
                    Url = x.Url,
                    CreatedAt = x.CreatedAt,
                };
            }).ToList();
            if (module is not null && existing.Position is not null)
            {
                await module.InvokeVoidAsync("removeNoteMarker", editingId);
                await module.InvokeVoidAsync("addNoteMarker",
                    editingId, existing.Position.Latitude, existing.Position.Longitude,
                    title, description,
                    existing.CreatedAt?.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
            }
            Toasts.Success($"Saved note '{title}'");
            return;
        }

        // Create branch: POST at the context-menu position.
        ApiResult<string> r;
        try { r = await NoteApi.CreateAsync(title, description, contextMenuLat, contextMenuLon); }
        catch (Exception ex) { Toasts.LogException(ex, $"Save note '{title}'"); return; }

        if (!r.Success || string.IsNullOrEmpty(r.Value))
        {
            Toasts.Error($"Save note '{title}' failed: {r.Error ?? "server rejected"}");
            return;
        }
        string id = r.Value;

        if (module is not null)
        {
            // CreatedAt: stamp with the same instant the server saw
            // (close enough to the server's actual createdAt that the
            // round-trip on next reload will agree). The marker
            // popup formats this for display; ISO-8601 round-trips
            // through JSInterop without DateTime fuzz.
            string createdAtIso = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
            await module.InvokeVoidAsync("addNoteMarker",
                id, contextMenuLat, contextMenuLon, title, description, createdAtIso);
        }
        loadedNotes = await SafeLoad(() => NoteApi.GetAllAsync(), "notes") ?? loadedNotes;
        Toasts.Success($"Saved note '{title}'");
    }

    private void CancelNoteDialog()
    {
        noteDialogVisible = false;
        noteEditId = null;
    }

    /// <summary>Invoked from the JS popup's Delete button. Round-trips
    /// DELETE to the server, then removes the marker. Called via
    /// <c>dotNetRef.invokeMethodAsync('DeleteNote', id)</c>.</summary>
    [JSInvokable]
    public async Task DeleteNote(string id)
    {
        string label = loadedNotes.FirstOrDefault(n => n.Id == id)?.Title ?? id;
        ApiResult r;
        try { r = await NoteApi.DeleteAsync(id); }
        catch (Exception ex) { Toasts.LogException(ex, $"Delete note '{label}'"); return; }
        if (!r.Success) { Toasts.Error($"Delete note '{label}' failed: {r.Error ?? "server rejected"}"); return; }

        if (module is not null)
            await module.InvokeVoidAsync("removeNoteMarker", id);
        loadedNotes.RemoveAll(n => n.Id == id);
        Toasts.Info("Note deleted");
    }

    /// <summary>Invoked from the JS popup's Go button: PUT the note's
    /// position as the SignalK course destination. Mirrors the
    /// layers-panel NavigateToNote on Map.razor; lives here as a
    /// JSInvokable so the popup-side action can call straight from
    /// the marker without round-tripping through the parent page's
    /// EventCallback chain.</summary>
    [JSInvokable]
    public async Task NoteGoTo(string id)
    {
        var note = loadedNotes.FirstOrDefault(n => n.Id == id);
        if (note?.Position is null)
        {
            Toasts.Warning("Note not found"); return;
        }
        try
        {
            var r = await CourseApi.SetDestinationPositionAsync(note.Position.Latitude, note.Position.Longitude);
            if (!r.Success)
            {
                Toasts.Error($"Navigate failed: {r.Error ?? "server rejected"}");
                return;
            }
            Toasts.Success($"Navigating to {(string.IsNullOrWhiteSpace(note.Title) ? "note" : note.Title)}");
        }
        catch (Exception ex) { Toasts.LogException(ex, "Navigate"); }
    }

    /// <summary>Invoked from the JS popup's Edit button: open the
    /// same dialog used for Add Note, pre-filled with the current
    /// title + description. Helm field-feedback was that the
    /// previous single-field PromptAsync rename hid the description
    /// and forced two trips (one to rename, a separate flow to edit
    /// description); the reused dialog gives a single edit surface.
    /// </summary>
    [JSInvokable]
    public Task NoteEdit(string id)
    {
        var note = loadedNotes.FirstOrDefault(n => n.Id == id);
        if (note is null) { Toasts.Warning("Note not found"); return Task.CompletedTask; }
        OpenNoteEditDialog(note);
        StateHasChanged();
        return Task.CompletedTask;
    }

    /// <summary>Invoked from the JS popup's Share button: build a
    /// GeoJSON Feature for the note (point geometry + title /
    /// description / createdAt properties) and hand it to
    /// <see cref="ShareService"/>. The helm gets the system share
    /// sheet on iPad / Android Chrome; on the desktop the JSON
    /// copies to clipboard with a toast.</summary>
    [JSInvokable]
    public async Task NoteShare(string id)
    {
        var note = loadedNotes.FirstOrDefault(n => n.Id == id);
        if (note?.Position is null) { Toasts.Warning("Note not found"); return; }

        // Inline GeoJSON build: notes are a small one-off shape, no
        // benefit to reusing ResourceExporter (which targets routes /
        // tracks). Coordinates per GeoJSON spec are [lon, lat].
        var feature = new GeoJsonShareNoteFeature(
            "Feature",
            new GeoJsonPointGeometry("Point", [note.Position.Longitude, note.Position.Latitude]),
            new GeoJsonShareNoteProperties(
                Title: note.Title,
                Description: note.Description,
                CreatedAt: note.CreatedAt?.ToString("o", System.Globalization.CultureInfo.InvariantCulture)));
        string json = System.Text.Json.JsonSerializer.Serialize(
            feature, OnaGeoJsonContext.Default.GeoJsonShareNoteFeature);
        string title = string.IsNullOrWhiteSpace(note.Title) ? "Note" : note.Title!;
        await ShareService.ShareJsonAsync(title, json);
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
        if (_resources is null) return;
        await _resources.SetNotesVisibleAsync(visible, loadedNotes);
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
    /// <summary>Hazard flag picked in the Add Region dialog. Applies
    /// to both Circle and Polygon paths so the helm sets it once
    /// up front rather than discovering on save that the polygon
    /// route hides it inside the edit panel and the circle route
    /// can't set it at all (focus-group field report). Defaults
    /// false; reset on every dialog open.</summary>
    private bool newRegionIsHazard;
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
        newRegionIsHazard = false;
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
                contextMenuLat, contextMenuLon, newRegionRadiusMeters,
                newRegionIsHazard);
        }
        catch (Exception ex) { Toasts.LogException(ex, $"Save region '{title}'"); return; }
        if (!r.Success || string.IsNullOrEmpty(r.Value))
        {
            Toasts.Error($"Save region '{title}' failed: {r.Error ?? "server rejected"}");
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
        string label = loadedRegions.FirstOrDefault(rg => rg.Id == id)?.Name ?? id;
        ApiResult r;
        try { r = await RegionApi.DeleteAsync(id); }
        catch (Exception ex) { Toasts.LogException(ex, $"Delete region '{label}'"); return; }
        if (!r.Success) { Toasts.Error($"Delete region '{label}' failed: {r.Error ?? "server rejected"}"); return; }

        if (module is not null)
            await module.InvokeVoidAsync("removeRegion", id);
        loadedRegions.RemoveAll(rg => rg.Id == id);
        Toasts.Info($"Region '{label}' deleted");
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
        catch (Exception ex) { Toasts.LogException(ex, $"Skip to WP {wpNumber}"); }
    }

    [JSInvokable]
    public async Task DeleteRouteById(string id)
    {
        string label = availableRoutes.FirstOrDefault(rt => rt.Id == id)?.Name ?? id;
        ApiResult r;
        try { r = await RouteApi.DeleteAsync(id); }
        catch (Exception ex) { Toasts.LogException(ex, $"Delete route '{label}'"); return; }
        if (!r.Success) { Toasts.Error($"Delete route '{label}' failed: {r.Error ?? "server rejected"}"); return; }

        // Strip from enabled + draw order so the UI forgets it too.
        if (enabledRoutes.Remove(id))
            await Settings.SetEnabledRoutesAsync(enabledRoutes);

        if (module is not null)
            try { await module.InvokeVoidAsync("removeRoute", id); }
            catch (JSDisconnectedException) { }

        availableRoutes.RemoveAll(rt => rt.Id == id);
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
                if (Data.AnchorActive)
                {
                    Toasts.Show("Anchor still active -- raise it before getting under way to silence the drag alarm",
                        ToastLevel.Info, durationSec: 8);
                }
                // Force an immediate route draw instead of waiting for
                // the next delta tick to notice the href change. The
                // href-diff lives in ActiveRouteSync; invalidate its
                // cached href so the next tick is guaranteed to
                // refetch, and kick the sync synchronously so the user
                // sees the polyline appear right away.
                _activeRouteSync?.InvalidateActiveRouteHref();
                if (_activeRouteSync is not null)
                    await _activeRouteSync.SyncAsync(Data, enabledRoutes, availableRoutes);
            }
            else Toasts.Error($"Start route failed: {r.Error ?? "server rejected"}");
        }
        catch (Exception ex) { Toasts.LogException(ex, "Start route"); }
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
        if (_resources is null) return;
        await _resources.SetRegionsVisibleAsync(visible, loadedRegions);
    }

}
