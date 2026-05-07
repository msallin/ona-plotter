using OnaPlotter.Models;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Services.Places;

/// <summary>
/// In-memory substring index over the helm's own resources -
/// waypoints, notes, regions. Surface a quick substring match in
/// the topbar place-search dropdown so the helm finds their saved
/// "Anchorage Cay" without leaving the search box for the layers
/// panel.
///
/// <para>Lifecycle: lazy-load on first <see cref="SearchAsync"/>
/// via the underlying *Api singletons. Index entries are cached for
/// <see cref="StaleTtl"/> minutes; subsequent calls within that
/// window read the cache. <see cref="Invalidate"/> drops the cache
/// so a CRUD that lands a new waypoint can call it and the next
/// search picks up the fresh row.</para>
///
/// <para>Source tags on each <see cref="PlaceResult"/> - "waypoint",
/// "note", "region" - let the SearchBox render a tiny badge so
/// the helm can tell at a glance whether a match comes from their
/// vault or an online geocoder.</para>
/// </summary>
public sealed class OwnPlacesIndex : IPlaceSearchService
{
    Task<IReadOnlyList<PlaceResult>> IPlaceSearchService.SearchAsync(string query, CancellationToken ct) =>
        SearchAsync(query, ct);

    /// <summary>Cache lifetime for the index. 5 minutes is short
    /// enough that a helm waypoint added on another plotter shows
    /// up reasonably soon without an explicit refresh, and long
    /// enough that typing-while-thinking doesn't trigger three
    /// parallel API loads.</summary>
    public static readonly TimeSpan StaleTtl = TimeSpan.FromMinutes(5);

    private readonly IWaypointApi _waypoints;
    private readonly INoteApi _notes;
    private readonly IRegionApi _regions;
    private readonly TimeProvider _time;
    private readonly ILogger<OwnPlacesIndex> _logger;

    /// <summary>Cached entries from the last successful load. Empty
    /// list (not null) once <see cref="EnsureLoadedAsync"/> has
    /// completed at least one pass; the null sentinel means "we
    /// have never loaded".</summary>
    private List<PlaceResult>? _entries;
    private DateTime _lastLoadUtc = DateTime.MinValue;
    /// <summary>One-shot guard so concurrent first-call refreshes
    /// don't trigger three parallel API fetches each time the helm
    /// types into an empty search box.</summary>
    private Task? _inFlightLoad;
    /// <summary>Generation counter bumped by <see cref="Invalidate"/>.
    /// An in-flight <see cref="LoadAsync"/> snapshots this at entry
    /// and only commits its result if the generation hasn't moved -
    /// otherwise the load completed against a now-stale view (a CRUD
    /// landed mid-flight) and its result is discarded so the next
    /// EnsureLoadedAsync starts a fresh load. Without this the
    /// pre-Invalidate load would resurrect stale data and reset the
    /// timestamp, defeating the whole point of the public Invalidate
    /// handle.</summary>
    private int _generation;

    public OwnPlacesIndex(
        IWaypointApi waypoints,
        INoteApi notes,
        IRegionApi regions,
        ILogger<OwnPlacesIndex> logger,
        TimeProvider? time = null)
    {
        _waypoints = waypoints;
        _notes = notes;
        _regions = regions;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Substring-match the cached index against <paramref name="query"/>.
    /// Case-insensitive on the helm-typed name + on the entry's
    /// stored name. Returns at most <paramref name="limit"/> rows
    /// in source-then-name order so the dropdown reads
    /// waypoints-first, then notes, then regions.
    /// </summary>
    public async Task<IReadOnlyList<PlaceResult>> SearchAsync(
        string query, CancellationToken ct = default, int limit = 10)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        await EnsureLoadedAsync(ct);
        if (_entries is null || _entries.Count == 0) return [];

        var needle = query.Trim();
        var results = new List<PlaceResult>();
        foreach (var entry in _entries)
        {
            if (entry.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(entry);
                if (results.Count >= limit) break;
            }
        }
        return results;
    }

    /// <summary>Drop the cached index so the next <see cref="SearchAsync"/>
    /// re-fetches. CRUD handlers (add waypoint, delete note, etc.)
    /// should call this so the search reflects the change without
    /// waiting on the TTL.</summary>
    public void Invalidate()
    {
        _entries = null;
        _lastLoadUtc = DateTime.MinValue;
        // Bump the generation so any in-flight LoadAsync that was
        // started against the pre-Invalidate view discards its result
        // when it completes - otherwise the pre-CRUD data would
        // resurrect itself with a fresh timestamp and the next
        // SearchAsync would return stale rows for up to StaleTtl.
        _generation++;
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (_entries is not null && now - _lastLoadUtc < StaleTtl) return;

        // Coalesce concurrent first-call loads. Two SearchAsync calls
        // landing within the same render pass would otherwise both
        // see _entries==null and both kick off three API fetches.
        if (_inFlightLoad is not null)
        {
            await _inFlightLoad;
            return;
        }

        _inFlightLoad = LoadAsync(ct);
        try { await _inFlightLoad; }
        finally { _inFlightLoad = null; }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        // Snapshot the generation at entry; if Invalidate() bumps it
        // before our awaits return we discard the result rather than
        // overwriting fresh post-CRUD state with the pre-CRUD view we
        // started loading.
        int generationAtEntry = _generation;

        // Three parallel API calls. Failure on any one degrades to
        // an empty contribution from that source; the others still
        // populate. Helm with a missing notes plugin shouldn't lose
        // their waypoint search.
        var waypointTask = SafeFetchAsync(() => _waypoints.GetAllAsync(ct), "waypoints");
        var noteTask = SafeFetchAsync(() => _notes.GetAllAsync(ct), "notes");
        var regionTask = SafeFetchAsync(() => _regions.GetAllAsync(ct), "regions");
        await Task.WhenAll(waypointTask, noteTask, regionTask);

        var fresh = new List<PlaceResult>();
        foreach (var w in waypointTask.Result ?? Enumerable.Empty<SignalkWaypoint>())
        {
            if (TryMapWaypoint(w) is { } r) fresh.Add(r);
        }
        foreach (var n in noteTask.Result ?? Enumerable.Empty<SignalkNote>())
        {
            if (TryMapNote(n) is { } r) fresh.Add(r);
        }
        foreach (var g in regionTask.Result ?? Enumerable.Empty<SignalkRegion>())
        {
            if (TryMapRegion(g) is { } r) fresh.Add(r);
        }

        // Generation check: if Invalidate() ran while we awaited the
        // three API calls, our `fresh` snapshot is from BEFORE the
        // CRUD that triggered the invalidate. Drop it so the next
        // EnsureLoadedAsync starts a new load against the post-CRUD
        // view. _entries stays null (Invalidate set it so) and
        // _lastLoadUtc stays MinValue so the next pass triggers.
        if (_generation != generationAtEntry)
        {
            _logger.LogInformation(
                "[own-places] dropping stale load (generation moved {From} -> {To})",
                generationAtEntry, _generation);
            return;
        }

        // Cancellation check: a fresh keystroke cancels the CTS used
        // by the API calls; SafeFetchAsync caught the resulting
        // exceptions and returned null, so `fresh` is empty even
        // though the underlying API never actually answered. Don't
        // commit that empty snapshot to _entries with a fresh TTL -
        // doing so would block real own-data hits from showing up
        // for the next StaleTtl window. Leaving _entries null forces
        // the next non-cancelled SearchAsync to retry the load.
        if (ct.IsCancellationRequested)
        {
            _logger.LogDebug("[own-places] dropping cancelled load");
            return;
        }

        _entries = fresh;
        _lastLoadUtc = _time.GetUtcNow().UtcDateTime;
    }

    private async Task<IReadOnlyList<T>?> SafeFetchAsync<T>(
        Func<Task<List<T>>> fetch, string label)
    {
        try { return await fetch(); }
        catch (Exception ex)
        {
            // Log the exception OBJECT (type + stack) instead of just
            // ex.Message so 3am triage can tell apart 401 (logged out),
            // network timeout, and a null-deref bug in the *Api - a
            // bare ".Message" collapses these into the same line.
            _logger.LogWarning(ex, "[own-places] {Label} fetch failed", label);
            return null;
        }
    }

    internal static PlaceResult? TryMapWaypoint(SignalkWaypoint w)
    {
        if (string.IsNullOrWhiteSpace(w.Name)) return null;
        if (w.Latitude is not double lat || w.Longitude is not double lon) return null;
        if (!double.IsFinite(lat) || !double.IsFinite(lon)) return null;
        if (lat < -90 || lat > 90 || lon < -180 || lon > 180) return null;
        return new PlaceResult(
            Name: w.Name!,
            DisplayLabel: w.Name!,    // own-data display = name; the source badge carries the type
            Lat: lat,
            Lon: lon,
            Source: "waypoint");
    }

    internal static PlaceResult? TryMapNote(SignalkNote n)
    {
        if (string.IsNullOrWhiteSpace(n.Title)) return null;
        if (n.Position is null) return null;
        var lat = n.Position.Latitude;
        var lon = n.Position.Longitude;
        if (!double.IsFinite(lat) || !double.IsFinite(lon)) return null;
        if (lat < -90 || lat > 90 || lon < -180 || lon > 180) return null;
        return new PlaceResult(
            Name: n.Title!,
            DisplayLabel: n.Title!,
            Lat: lat,
            Lon: lon,
            Source: "note");
    }

    internal static PlaceResult? TryMapRegion(SignalkRegion r)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) return null;
        if (r.OuterRings.Count == 0) return null;
        // Centroid of the first outer ring as the representative
        // lat/lon. A more accurate "label point" is the polygon
        // pole-of-inaccessibility, but simple centroid is good
        // enough for a flyTo target on a region the helm already
        // knows by name.
        var ring = r.OuterRings[0];
        if (ring.Length == 0) return null;
        double sumLat = 0, sumLon = 0;
        int n = 0;
        // Convention pin: SignalK regions store each ring point as
        // [lat, lon] (Leaflet order), NOT GeoJSON [lon, lat]. Naming
        // the columns explicitly avoids a 3am misread when this code
        // is held next to PhotonPlaceSearchService where coordinates
        // arrive in the GeoJSON order.
        foreach (var pt in ring)
        {
            if (pt.Length < 2) continue;
            double pointLat = pt[0];
            double pointLon = pt[1];
            sumLat += pointLat;
            sumLon += pointLon;
            n++;
        }
        if (n == 0) return null;
        double lat = sumLat / n;
        double lon = sumLon / n;
        if (!double.IsFinite(lat) || !double.IsFinite(lon)) return null;
        if (lat < -90 || lat > 90 || lon < -180 || lon > 180) return null;
        return new PlaceResult(
            Name: r.Name!,
            DisplayLabel: r.Name!,
            Lat: lat,
            Lon: lon,
            Source: "region");
    }
}
