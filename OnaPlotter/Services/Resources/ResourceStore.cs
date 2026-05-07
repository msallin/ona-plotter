using System.Text.Json;
using OnaPlotter.Models;
using OnaPlotter.Services.Api;
using OnaPlotter.Utilities;

namespace OnaPlotter.Services.Resources;

/// <summary>
/// Single source of truth for SignalK <c>resources.{routes,waypoints,
/// notes,regions}.*</c> on the helm. Composition over <see cref="SignalkClient"/>
/// (subscribes to its <see cref="SignalkClient.OnResourceDelta"/>) and
/// over the four <c>*Api</c> REST clients (initial load + reconnect
/// reconcile). Both pages that previously did their own REST polling --
/// <c>Map.razor</c> and <c>Resources.razor</c> -- now read from this
/// store and subscribe to its typed change events.
///
/// <para><b>Why this exists:</b> the helm reported a multi-plotter
/// sync bug where editing a route on plotter A left plotter B's chart
/// stuck on the pre-edit geometry, even though the active-WP line
/// followed the new endpoint. Root cause: OnaPlotter never subscribed
/// to <c>resources.*</c> deltas, so the only refresh path was a local
/// re-fetch after the user's OWN save. The signalk-server emits
/// resource changes as ordinary deltas (full document on PUT/POST,
/// <c>null</c> on DELETE) -- this store is the consumer that closes
/// the loop. Same pattern as Freeboard-SK: WS delta is the
/// invalidation signal + cache fill, REST reconcile on reconnect
/// backfills any deltas missed during the disconnect window.</para>
///
/// <para><b>Wire shape</b> per signalk-server's
/// <c>buildDeltaMsg</c>: path = <c>resources.&lt;type&gt;.&lt;id&gt;</c>,
/// value = full resource document on create / update, <c>null</c> on
/// delete. <see cref="SignalkClient.DispatchResourceUpdates"/> peels
/// the path into (type, id, value) and re-fires as
/// <see cref="SignalkClient.OnResourceDelta"/>; this store branches
/// on type and parses value into the same DTO that the REST API
/// returns.</para>
/// </summary>
public sealed class ResourceStore : IAsyncDisposable
{
    private readonly IRouteApi _routes;
    private readonly IWaypointApi _waypoints;
    private readonly INoteApi _notes;
    private readonly IRegionApi _regions;
    private readonly SignalkClient _signalk;
    private readonly ILogger<ResourceStore> _logger;

    // Per-type caches keyed by resource id (the segment after
    // resources.<type>. on the wire, also the URL slug for REST
    // GET /resources/<type>/<id>). Dictionary<,> isn't thread-safe,
    // but Blazor WASM is single-threaded so the lock-free read +
    // mutate-on-event pattern is fine. The cached lists snapshot
    // these on every Routes / Waypoints / ... read.
    private readonly Dictionary<string, SignalkRoute> _routeById = new();
    private readonly Dictionary<string, SignalkWaypoint> _waypointById = new();
    private readonly Dictionary<string, SignalkNote> _noteById = new();
    private readonly Dictionary<string, SignalkRegion> _regionById = new();

    /// <summary>True once <see cref="RefreshAllAsync"/> has completed
    /// at least one full pass. Components consult this to decide
    /// whether to render "loading" placeholders or the cached values.
    /// Set after the first REST batch lands so a delta-only race
    /// (helm gets a delta before the initial REST returns) doesn't
    /// flip it true with partial state.</summary>
    public bool IsLoaded { get; private set; }

    public ResourceStore(
        IRouteApi routes,
        IWaypointApi waypoints,
        INoteApi notes,
        IRegionApi regions,
        SignalkClient signalk,
        ILogger<ResourceStore> logger)
    {
        _routes = routes;
        _waypoints = waypoints;
        _notes = notes;
        _regions = regions;
        _signalk = signalk;
        _logger = logger;

        _signalk.OnResourceDelta += HandleResourceDelta;
        _signalk.OnConnectionChanged += HandleConnectionChange;
    }

    // --- Public read snapshots ---------------------------------------

    /// <summary>Snapshot of all routes currently in the cache. New list
    /// each call so a consumer can iterate safely while a delta arrives
    /// mid-iteration. Order is dictionary-iteration order (effectively
    /// insertion order on .NET; not guaranteed but stable enough for
    /// helm-facing lists).</summary>
    public IReadOnlyList<SignalkRoute> Routes
    {
        get
        {
            var snapshot = new List<SignalkRoute>(_routeById.Count);
            snapshot.AddRange(_routeById.Values);
            return snapshot;
        }
    }

    public IReadOnlyList<SignalkWaypoint> Waypoints
    {
        get
        {
            var snapshot = new List<SignalkWaypoint>(_waypointById.Count);
            snapshot.AddRange(_waypointById.Values);
            return snapshot;
        }
    }

    public IReadOnlyList<SignalkNote> Notes
    {
        get
        {
            var snapshot = new List<SignalkNote>(_noteById.Count);
            snapshot.AddRange(_noteById.Values);
            return snapshot;
        }
    }

    public IReadOnlyList<SignalkRegion> Regions
    {
        get
        {
            var snapshot = new List<SignalkRegion>(_regionById.Count);
            snapshot.AddRange(_regionById.Values);
            return snapshot;
        }
    }

    public SignalkRoute? GetRoute(string id) =>
        _routeById.TryGetValue(id, out var r) ? r : null;

    public SignalkWaypoint? GetWaypoint(string id) =>
        _waypointById.TryGetValue(id, out var w) ? w : null;

    public SignalkNote? GetNote(string id) =>
        _noteById.TryGetValue(id, out var n) ? n : null;

    public SignalkRegion? GetRegion(string id) =>
        _regionById.TryGetValue(id, out var r) ? r : null;

    // --- Change events (per type) ------------------------------------
    //
    // Subscribers must handle thread-affinity themselves (Blazor
    // components: InvokeAsync(StateHasChanged) wrap). Removed events
    // fire AFTER the entry is dropped from the cache, so a handler
    // that re-checks via GetRoute(id) gets null.

    /// <summary>Fires when a route is created or updated. Argument is
    /// the resource id; the new document is in the cache when this
    /// fires (look it up via <see cref="GetRoute"/>).</summary>
    public event Action<string>? OnRouteChanged;

    /// <summary>Fires when a route is deleted (delta value=null OR a
    /// REST reconcile finds the id is no longer in the server's
    /// list). Cache entry is already gone when this fires.</summary>
    public event Action<string>? OnRouteRemoved;

    public event Action<string>? OnWaypointChanged;
    public event Action<string>? OnWaypointRemoved;

    public event Action<string>? OnNoteChanged;
    public event Action<string>? OnNoteRemoved;

    public event Action<string>? OnRegionChanged;
    public event Action<string>? OnRegionRemoved;

    /// <summary>One coalesced event for "a refresh just landed". Useful
    /// for components that want to re-render their full list after the
    /// REST reconcile rather than wiring four typed handlers.</summary>
    public event Action? OnReloaded;

    // --- REST reconcile ----------------------------------------------

    /// <summary>
    /// Full REST reload of all four resource types in parallel. Replaces
    /// the in-memory cache with the server's authoritative snapshot;
    /// fires per-type Changed events for additions, Removed events for
    /// ids dropped from the server, and a final <see cref="OnReloaded"/>
    /// at the end.
    ///
    /// <para>Called from app startup (after DI build) and from
    /// <see cref="HandleConnectionChange"/> on every WS reconnect to
    /// backfill deltas missed during the disconnect window. Per-type
    /// failures (one API down) degrade gracefully: that type keeps its
    /// previous cache; other types refresh normally.</para>
    /// </summary>
    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        var routesTask = SafeFetch(() => _routes.GetAllAsync(ct), "routes");
        var waypointsTask = SafeFetch(() => _waypoints.GetAllAsync(ct), "waypoints");
        var notesTask = SafeFetch(() => _notes.GetAllAsync(ct), "notes");
        var regionsTask = SafeFetch(() => _regions.GetAllAsync(ct), "regions");
        await Task.WhenAll(routesTask, waypointsTask, notesTask, regionsTask);

        if (routesTask.Result is { } routes) ReplaceRoutes(routes);
        if (waypointsTask.Result is { } waypoints) ReplaceWaypoints(waypoints);
        if (notesTask.Result is { } notes) ReplaceNotes(notes);
        if (regionsTask.Result is { } regions) ReplaceRegions(regions);

        IsLoaded = true;
        try { OnReloaded?.Invoke(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[resources] OnReloaded subscriber threw");
        }
    }

    private async Task<List<T>?> SafeFetch<T>(Func<Task<List<T>>> fetch, string label)
    {
        try { return await fetch(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[resources] {Label} REST fetch failed", label);
            return null;
        }
    }

    // --- Replace-cache helpers (REST reconcile path) ------------------

    private void ReplaceRoutes(List<SignalkRoute> fresh)
    {
        var newIds = new HashSet<string>(fresh.Count);
        foreach (var r in fresh)
        {
            if (string.IsNullOrEmpty(r.Id)) continue;
            newIds.Add(r.Id);
            _routeById[r.Id] = r;
            try { OnRouteChanged?.Invoke(r.Id); }
            catch (Exception ex) { _logger.LogWarning(ex, "[resources] route changed handler threw"); }
        }
        // Remove any cached entries no longer in the server's list.
        var stale = _routeById.Keys.Where(k => !newIds.Contains(k)).ToList();
        foreach (var id in stale)
        {
            _routeById.Remove(id);
            try { OnRouteRemoved?.Invoke(id); }
            catch (Exception ex) { _logger.LogWarning(ex, "[resources] route removed handler threw"); }
        }
    }

    private void ReplaceWaypoints(List<SignalkWaypoint> fresh)
    {
        var newIds = new HashSet<string>(fresh.Count);
        foreach (var w in fresh)
        {
            if (string.IsNullOrEmpty(w.Id)) continue;
            newIds.Add(w.Id);
            _waypointById[w.Id] = w;
            try { OnWaypointChanged?.Invoke(w.Id); }
            catch (Exception ex) { _logger.LogWarning(ex, "[resources] waypoint changed handler threw"); }
        }
        var stale = _waypointById.Keys.Where(k => !newIds.Contains(k)).ToList();
        foreach (var id in stale)
        {
            _waypointById.Remove(id);
            try { OnWaypointRemoved?.Invoke(id); }
            catch (Exception ex) { _logger.LogWarning(ex, "[resources] waypoint removed handler threw"); }
        }
    }

    private void ReplaceNotes(List<SignalkNote> fresh)
    {
        var newIds = new HashSet<string>(fresh.Count);
        foreach (var n in fresh)
        {
            if (string.IsNullOrEmpty(n.Id)) continue;
            newIds.Add(n.Id);
            _noteById[n.Id] = n;
            try { OnNoteChanged?.Invoke(n.Id); }
            catch (Exception ex) { _logger.LogWarning(ex, "[resources] note changed handler threw"); }
        }
        var stale = _noteById.Keys.Where(k => !newIds.Contains(k)).ToList();
        foreach (var id in stale)
        {
            _noteById.Remove(id);
            try { OnNoteRemoved?.Invoke(id); }
            catch (Exception ex) { _logger.LogWarning(ex, "[resources] note removed handler threw"); }
        }
    }

    private void ReplaceRegions(List<SignalkRegion> fresh)
    {
        var newIds = new HashSet<string>(fresh.Count);
        foreach (var r in fresh)
        {
            if (string.IsNullOrEmpty(r.Id)) continue;
            newIds.Add(r.Id);
            _regionById[r.Id] = r;
            try { OnRegionChanged?.Invoke(r.Id); }
            catch (Exception ex) { _logger.LogWarning(ex, "[resources] region changed handler threw"); }
        }
        var stale = _regionById.Keys.Where(k => !newIds.Contains(k)).ToList();
        foreach (var id in stale)
        {
            _regionById.Remove(id);
            try { OnRegionRemoved?.Invoke(id); }
            catch (Exception ex) { _logger.LogWarning(ex, "[resources] region removed handler threw"); }
        }
    }

    // --- Delta apply (WS push path) -----------------------------------

    /// <summary>
    /// Apply a single <c>resources.&lt;type&gt;.&lt;id&gt;</c> delta to
    /// the cache. <see cref="JsonElement.ValueKind"/> == Null is the
    /// delete signal (DTO can't fully represent it; treat as removal
    /// from the cache). Any non-Null value is parsed into the typed
    /// record same as REST returns.
    /// </summary>
    internal void HandleResourceDelta(string type, string id, JsonElement value)
    {
        if (string.IsNullOrEmpty(id)) return;
        switch (type)
        {
            case "routes": HandleRouteDelta(id, value); break;
            case "waypoints": HandleWaypointDelta(id, value); break;
            case "notes": HandleNoteDelta(id, value); break;
            case "regions": HandleRegionDelta(id, value); break;
            default:
                // Unknown type (e.g. resources.charts.* if a future
                // server emits it). Log once at debug; not an error.
                _logger.LogDebug("[resources] ignoring delta for unknown type '{Type}/{Id}'", type, id);
                break;
        }
    }

    private void HandleRouteDelta(string id, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            if (_routeById.Remove(id))
            {
                try { OnRouteRemoved?.Invoke(id); }
                catch (Exception ex) { _logger.LogWarning(ex, "[resources] route removed handler threw"); }
            }
            return;
        }
        try
        {
            var route = value.Deserialize<SignalkRoute>();
            if (route is null) return;
            route.Id = id;
            // Server-emitted route docs may carry geometry types other
            // than LineString; the helm-facing surface only renders
            // LineStrings (matches RouteApi.GetAllAsync's filter).
            if (route.Feature?.Geometry?.Type is not "LineString") return;
            _routeById[id] = route;
            try { OnRouteChanged?.Invoke(id); }
            catch (Exception ex) { _logger.LogWarning(ex, "[resources] route changed handler threw"); }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[resources] route delta parse failed for id '{Id}'", id);
        }
    }

    private void HandleWaypointDelta(string id, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            if (_waypointById.Remove(id))
            {
                try { OnWaypointRemoved?.Invoke(id); }
                catch (Exception ex) { _logger.LogWarning(ex, "[resources] waypoint removed handler threw"); }
            }
            return;
        }
        try
        {
            var wp = value.Deserialize<SignalkWaypoint>();
            if (wp is null) return;
            wp.Id = id;
            // Mirror WaypointApi.GetAllAsync's coordinate + description
            // extraction so consumers see the same shape regardless of
            // whether this entry came from a delta or a REST fetch.
            var coords = wp.Feature?.Geometry?.Coordinates;
            if (coords is not null)
            {
                var latLon = coords.Value.ToLatLonPoint();
                if (latLon is not null)
                {
                    wp.Latitude = latLon.Value.Latitude;
                    wp.Longitude = latLon.Value.Longitude;
                }
            }
            var desc = wp.Feature?.Properties?.Description;
            wp.Description = string.IsNullOrEmpty(desc) ? null : desc;
            _waypointById[id] = wp;
            try { OnWaypointChanged?.Invoke(id); }
            catch (Exception ex) { _logger.LogWarning(ex, "[resources] waypoint changed handler threw"); }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[resources] waypoint delta parse failed for id '{Id}'", id);
        }
    }

    private void HandleNoteDelta(string id, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            if (_noteById.Remove(id))
            {
                try { OnNoteRemoved?.Invoke(id); }
                catch (Exception ex) { _logger.LogWarning(ex, "[resources] note removed handler threw"); }
            }
            return;
        }
        try
        {
            var note = value.Deserialize<SignalkNote>();
            if (note is null) return;
            note.Id = id;
            _noteById[id] = note;
            try { OnNoteChanged?.Invoke(id); }
            catch (Exception ex) { _logger.LogWarning(ex, "[resources] note changed handler threw"); }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[resources] note delta parse failed for id '{Id}'", id);
        }
    }

    private void HandleRegionDelta(string id, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            if (_regionById.Remove(id))
            {
                try { OnRegionRemoved?.Invoke(id); }
                catch (Exception ex) { _logger.LogWarning(ex, "[resources] region removed handler threw"); }
            }
            return;
        }
        try
        {
            var region = value.Deserialize<SignalkRegion>();
            if (region is null) return;
            region.Id = id;
            _regionById[id] = region;
            try { OnRegionChanged?.Invoke(id); }
            catch (Exception ex) { _logger.LogWarning(ex, "[resources] region changed handler threw"); }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[resources] region delta parse failed for id '{Id}'", id);
        }
    }

    // --- Reconnect reconcile -----------------------------------------

    private bool _wasConnected;

    private void HandleConnectionChange()
    {
        // The OnConnectionChanged event doesn't carry old/new state, so
        // we track edges ourselves: false -> true means "just (re)connected"
        // and is when we want to backfill any deltas missed during the
        // disconnect window via REST.
        var nowConnected = _signalk.IsConnected;
        var transitionedToConnected = !_wasConnected && nowConnected;
        _wasConnected = nowConnected;
        if (!transitionedToConnected) return;

        // Fire-and-forget: the SignalkClient.OnConnectionChanged handler
        // chain is sync, and we don't want to block the WS-loop pump
        // on a multi-second REST reconcile.
        _ = Task.Run(async () =>
        {
            try { await RefreshAllAsync(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[resources] reconcile-on-reconnect failed");
            }
        });
    }

    public ValueTask DisposeAsync()
    {
        _signalk.OnResourceDelta -= HandleResourceDelta;
        _signalk.OnConnectionChanged -= HandleConnectionChange;
        return ValueTask.CompletedTask;
    }
}
