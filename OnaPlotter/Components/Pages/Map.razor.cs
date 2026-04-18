using Microsoft.JSInterop;
using OnaPlotter.Models;
using OnaPlotter.Services;
using OnaPlotter.Utilities;

namespace OnaPlotter.Components.Pages;

/// <summary>
/// Code-behind for the Map page. Holds the CRUD flow for the three
/// server-stored resources the user can create from the chart --
/// waypoints, notes, regions -- plus the weather-routing trigger.
/// These share a shape (context-menu entry -> modal dialog ->
/// POST -> JS marker draw) and moving them out of the .razor keeps
/// the markup file focused on layout and core map state.
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
    private List<SignalkWaypoint> loadedWaypoints = [];

    private void CreateWaypointHere()
    {
        contextMenuVisible = false;
        waypointDialogVisible = true;
        newWaypointName = "";
    }

    private async Task SaveWaypoint()
    {
        waypointDialogVisible = false;
        string name = string.IsNullOrWhiteSpace(newWaypointName) ? $"WPT {DateTime.Now:HH:mm}" : newWaypointName;
        string? id;
        try { id = await WaypointApi.CreateAsync(name, contextMenuLat, contextMenuLon); }
        catch (Exception ex) { Toasts.Error($"Save waypoint failed: {ex.Message}"); return; }

        if (id is null) { Toasts.Error("Save waypoint failed: server rejected"); return; }

        if (module is not null)
            await module.InvokeVoidAsync("addWaypointMarker", id, contextMenuLat, contextMenuLon, name);
        loadedWaypoints = await SafeLoad(() => WaypointApi.GetAllAsync(), "waypoints") ?? loadedWaypoints;
        Toasts.Success($"Saved waypoint '{name}'");
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
        string? id;
        try { id = await NoteApi.CreateAsync(title, description, contextMenuLat, contextMenuLon); }
        catch (Exception ex) { Toasts.Error($"Save note failed: {ex.Message}"); return; }

        if (id is null) { Toasts.Error("Save note failed: server rejected"); return; }

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
        try
        {
            var ok = await NoteApi.DeleteAsync(id);
            if (!ok) { Toasts.Error("Delete note failed: server rejected"); return; }
        }
        catch (Exception ex) { Toasts.Error($"Delete note failed: {ex.Message}"); return; }

        if (module is not null)
            await module.InvokeVoidAsync("removeNoteMarker", id);
        loadedNotes = loadedNotes.Where(n => n.Id != id).ToList();
        Toasts.Show("Note deleted", ToastService.ToastLevel.Info);
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

    private void CreateRegionHere()
    {
        contextMenuVisible = false;
        regionDialogVisible = true;
        newRegionTitle = "";
        newRegionDescription = "";
        newRegionRadiusMeters = 250;
    }

    private async Task SaveRegion()
    {
        regionDialogVisible = false;
        string title = string.IsNullOrWhiteSpace(newRegionTitle)
            ? $"Region {DateTime.Now:HH:mm}"
            : newRegionTitle;
        string description = newRegionDescription ?? "";
        string? id;
        try
        {
            id = await RegionApi.CreateCircleAsync(title, description,
                contextMenuLat, contextMenuLon, newRegionRadiusMeters);
        }
        catch (Exception ex) { Toasts.Error($"Save region failed: {ex.Message}"); return; }
        if (id is null) { Toasts.Error("Save region failed: server rejected"); return; }

        // Re-fetch so the new region has the same shape the server sent
        // back (id-from-key, Leaflet-ordered rings). Simpler than
        // locally-replicating the circle-polygon math for the optimistic
        // insert.
        loadedRegions = await SafeLoad(() => RegionApi.GetAllAsync(), "regions") ?? loadedRegions;
        var created = loadedRegions.FirstOrDefault(r => r.Id == id);
        if (created is not null && module is not null)
            await module.InvokeVoidAsync("addRegion",
                created.Id, created.OuterRings, created.Name, created.Description);
        Toasts.Success($"Saved region '{title}'");
    }

    [JSInvokable]
    public async Task DeleteRegion(string id)
    {
        try
        {
            var ok = await RegionApi.DeleteAsync(id);
            if (!ok) { Toasts.Error("Delete region failed: server rejected"); return; }
        }
        catch (Exception ex) { Toasts.Error($"Delete region failed: {ex.Message}"); return; }

        if (module is not null)
            await module.InvokeVoidAsync("removeRegion", id);
        loadedRegions = loadedRegions.Where(r => r.Id != id).ToList();
        Toasts.Show("Region deleted", ToastService.ToastLevel.Info);
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
        try
        {
            await module.InvokeVoidAsync("focusRegion", region.Id, region.OuterRings[0]);
        }
        catch (JSDisconnectedException) { }
        catch (ObjectDisposedException) { }
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

    // ---- Weather routing (isochrone over wind forecast) --------------
    /// <summary>
    /// Kicks off isochrone weather routing from the current own-boat
    /// position to the point the user right-clicked / long-pressed.
    /// Fetches a single-point wind forecast from Open-Meteo, runs the
    /// router with the user's uploaded polars, and draws the result on
    /// the map. For v1 we assume uniform wind across the whole route --
    /// adequate for coastal hops up to ~30 nm; a multi-point grid
    /// sample is a later upgrade.
    /// </summary>
    private async Task RouteWithWindHere()
    {
        contextMenuVisible = false;
        if (!Polar.HasPolar) { Toasts.Error("Upload polars in Settings first"); return; }
        if (Data.Latitude is null || Data.Longitude is null)
        {
            Toasts.Error("Own-boat position unknown"); return;
        }

        double startLat = Data.Latitude.Value, startLon = Data.Longitude.Value;
        double endLat = contextMenuLat, endLon = contextMenuLon;
        DateTime now = DateTime.UtcNow;

        Toasts.Show("Computing weather route...", ToastService.ToastLevel.Info, durationSec: 2);

        var forecast = await WeatherApi.GetAsync(startLat, startLon, forecastHours: 24);
        if (forecast is null) { Toasts.Error("Weather forecast unavailable"); return; }

        // Single-point forecast applied uniformly across the route. The
        // router lookup returns the nearest-time sample from that series;
        // spatial variation is ignored for now.
        WindSample? WindAt(double _lat, double _lon, DateTime t) => forecast.At(t);
        double? PolarLookup(double twaDeg, double twsKn) => Polar.GetTargetSpeed(twaDeg, twsKn);

        var route = IsochroneRouter.Route(
            startLat, startLon, now,
            endLat, endLon,
            WindAt, PolarLookup,
            options: new IsochroneRouter.Options(StepMinutes: 10, MaxSteps: 72, ReachNauticalMiles: 0.4));

        if (route is null)
        {
            Toasts.Error("No viable route in the next 12 hours (tight no-go angle, or too far)");
            return;
        }

        var coords = route.Path.Select(w => new[] { w.Latitude, w.Longitude }).ToArray();

        // Wind-field overlay: sample the forecast at each 3rd waypoint
        // and pass to JS as [lat, lon, dirFromDeg, speedKn]. Enough points
        // to let the skipper eyeball where the wind swings along the
        // route without cluttering the chart with one arrow per step.
        // `DirectionDeg` is FROM-which (meteorological convention).
        var windSamples = new List<double[]>();
        for (int i = 0; i < route.Path.Count; i += 3)
        {
            var wp = route.Path[i];
            var sample = forecast.At(wp.Time);
            if (sample is null) continue;
            windSamples.Add([wp.Latitude, wp.Longitude, sample.Value.DirectionDeg, sample.Value.SpeedKn]);
        }

        if (module is not null)
            await module.InvokeVoidAsync("setWeatherRoute", (object)coords, (object)windSamples.ToArray());

        var eta = now + route.Duration;
        Toasts.Success(
            $"Weather route: {route.TotalNauticalMiles:F1} nm, "
            + $"ETA {eta.ToLocalTime():HH:mm} (+{route.Duration.Hours}h{route.Duration.Minutes:D2})");
    }
}
