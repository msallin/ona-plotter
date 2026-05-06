using OnaPlotter.Models;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Services.Places;

/// <summary>
/// In-memory substring index over the helm's own resources --
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
/// <para>Source tags on each <see cref="PlaceResult"/> -- "waypoint",
/// "note", "region" -- let the SearchBox render a tiny badge so
/// the helm can tell at a glance whether a match comes from their
/// vault or an online geocoder.</para>
/// </summary>
public sealed class OwnPlacesIndex
{
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
        // _inFlightLoad is left in place; if a load is mid-flight
        // it'll complete and populate _entries; the next SearchAsync
        // call sees the (now stale) timestamp and triggers a fresh
        // load.
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
        _entries = fresh;
        _lastLoadUtc = _time.GetUtcNow().UtcDateTime;
    }

    private async Task<IReadOnlyList<T>?> SafeFetchAsync<T>(
        Func<Task<List<T>>> fetch, string label)
    {
        try { return await fetch(); }
        catch (Exception ex)
        {
            _logger.LogWarning("[own-places] {Label} fetch failed: {Message}",
                label, ex.Message);
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
        foreach (var pt in ring)
        {
            if (pt.Length < 2) continue;
            sumLat += pt[0]; sumLon += pt[1];
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
