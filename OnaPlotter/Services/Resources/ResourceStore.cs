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
    // mutate-on-event pattern is fine. The Routes / Waypoints / ...
    // properties hand out cached snapshots that get invalidated to
    // null on every dictionary mutation; the next read rebuilds the
    // list. This avoids a fresh allocation on every event-handler
    // tick (Map.razor's HUD reads Routes / Waypoints repeatedly per
    // pan / state-changed cycle).
    private readonly Dictionary<string, SignalkRoute> _routeById = new();
    private readonly Dictionary<string, SignalkWaypoint> _waypointById = new();
    private readonly Dictionary<string, SignalkNote> _noteById = new();
    private readonly Dictionary<string, SignalkRegion> _regionById = new();

    // Snapshot caches. null means "rebuild on next read". Set back to
    // null in every code path that mutates the matching dictionary --
    // search for the InvalidateXxx calls below to verify coverage.
    // Old callers that captured a list reference keep iterating against
    // their snapshot safely; the next caller after a mutation sees the
    // freshly-rebuilt list.
    private List<SignalkRoute>? _routesSnapshot;
    private List<SignalkWaypoint>? _waypointsSnapshot;
    private List<SignalkNote>? _notesSnapshot;
    private List<SignalkRegion>? _regionsSnapshot;

    /// <summary>Coalesces concurrent <see cref="RefreshAllAsync"/>
    /// calls. Two callers (manual Refresh button + reconnect-edge
    /// reconcile) can land at the same moment on a flaky link; without
    /// the gate, two passes interleave their <c>ReplaceXxx</c>
    /// mutations and fire 2x the per-row Changed events. With it, the
    /// second caller awaits the first instead of re-issuing four
    /// REST round-trips.</summary>
    private Task? _inFlightRefresh;

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

    /// <summary>Snapshot of all routes currently in the cache. Cached
    /// across reads; invalidated on every dictionary mutation so a
    /// caller that holds onto an old reference continues to iterate
    /// the snapshot it was given (safe), and a fresh caller after a
    /// mutation gets a freshly rebuilt list. Order is
    /// dictionary-iteration order (effectively insertion order on
    /// .NET; not guaranteed but stable enough for helm-facing
    /// lists).</summary>
    public IReadOnlyList<SignalkRoute> Routes
    {
        get
        {
            if (_routesSnapshot is { } cached) return cached;
            var snapshot = new List<SignalkRoute>(_routeById.Count);
            snapshot.AddRange(_routeById.Values);
            _routesSnapshot = snapshot;
            return snapshot;
        }
    }

    public IReadOnlyList<SignalkWaypoint> Waypoints
    {
        get
        {
            if (_waypointsSnapshot is { } cached) return cached;
            var snapshot = new List<SignalkWaypoint>(_waypointById.Count);
            snapshot.AddRange(_waypointById.Values);
            _waypointsSnapshot = snapshot;
            return snapshot;
        }
    }

    public IReadOnlyList<SignalkNote> Notes
    {
        get
        {
            if (_notesSnapshot is { } cached) return cached;
            var snapshot = new List<SignalkNote>(_noteById.Count);
            snapshot.AddRange(_noteById.Values);
            _notesSnapshot = snapshot;
            return snapshot;
        }
    }

    public IReadOnlyList<SignalkRegion> Regions
    {
        get
        {
            if (_regionsSnapshot is { } cached) return cached;
            var snapshot = new List<SignalkRegion>(_regionById.Count);
            snapshot.AddRange(_regionById.Values);
            _regionsSnapshot = snapshot;
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

    /// <summary>Invoke a typed event with the standard
    /// "log + swallow" guard. Pulled out of the per-event sites so a
    /// subscriber-threw branch is one method instead of ~30 inline
    /// try/catch blocks. Multicast invocations: if subscriber A
    /// throws and subscriber B follows, B doesn't run -- mirrors the
    /// behaviour of the previous inline pattern (no foreach over
    /// GetInvocationList).</summary>
    private void SafeInvoke<T>(Action<T>? handler, T arg, string label)
    {
        if (handler is null) return;
        try { handler.Invoke(arg); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[resources] {Label} subscriber threw", label);
        }
    }

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
    ///
    /// <param name="cause">Free-form tag describing why the reconcile
    /// fired -- "startup", "reconnect", "page-mount", "manual",
    /// or whatever the caller wants. Surfaces in the structured
    /// reconcile-complete log so a helm reading the journal can tell
    /// "ah, the reconcile that just landed was the reconnect-edge
    /// one" without correlating timestamps.</param>
    /// </summary>
    public Task RefreshAllAsync(string cause = "manual", CancellationToken ct = default)
    {
        // Coalesce concurrent callers. Two paths can call this at the
        // same moment on a flaky link: the manual Refresh button and
        // the reconnect-edge reconcile. Returning the in-flight Task
        // means the second caller awaits the first's REST round-trip
        // (and observes the same per-type events) instead of issuing
        // 4 more parallel GETs and racing the cache mutations. The
        // second caller's cause string is dropped on the floor; the
        // first cause wins, which matches the actual story (the
        // first call did the work).
        if (_inFlightRefresh is { IsCompleted: false } running) return running;
        _inFlightRefresh = RefreshAllCoreAsync(cause, ct);
        return _inFlightRefresh;
    }

    /// <summary>Per-type reconcile breakdown so the
    /// reconcile-complete log can show added / updated / removed
    /// counts. "added" = id wasn't in the cache before the reconcile;
    /// "updated" = id was already there (reference replaced);
    /// "removed" = id was in the cache but not in the fresh list.</summary>
    private readonly record struct ReplaceCounts(int Added, int Updated, int Removed);

    private static readonly ReplaceCounts ReplaceCountsZero = new(0, 0, 0);

    private async Task RefreshAllCoreAsync(string cause, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("[resources] reconcile started cause={Cause}", cause);

        var routesTask = SafeFetch(() => _routes.GetAllAsync(ct), "routes");
        var waypointsTask = SafeFetch(() => _waypoints.GetAllAsync(ct), "waypoints");
        var notesTask = SafeFetch(() => _notes.GetAllAsync(ct), "notes");
        var regionsTask = SafeFetch(() => _regions.GetAllAsync(ct), "regions");
        await Task.WhenAll(routesTask, waypointsTask, notesTask, regionsTask);

        var routeCounts = routesTask.Result is { } routes ? ReplaceRoutes(routes) : ReplaceCountsZero;
        var waypointCounts = waypointsTask.Result is { } waypoints ? ReplaceWaypoints(waypoints) : ReplaceCountsZero;
        var noteCounts = notesTask.Result is { } notes ? ReplaceNotes(notes) : ReplaceCountsZero;
        var regionCounts = regionsTask.Result is { } regions ? ReplaceRegions(regions) : ReplaceCountsZero;

        bool firstLoad = !IsLoaded;
        IsLoaded = true;
        if (OnReloaded is { } reloaded)
        {
            try { reloaded.Invoke(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[resources] OnReloaded subscriber threw");
            }
        }

        _logger.LogInformation(
            "[resources] reconcile complete cause={Cause} in {ElapsedMs}ms first_load={FirstLoad} " +
            "routes(total={RouteCount} +{RouteAdded}/~{RouteUpdated}/-{RouteRemoved}) " +
            "waypoints(total={WpCount} +{WpAdded}/~{WpUpdated}/-{WpRemoved}) " +
            "notes(total={NoteCount} +{NoteAdded}/~{NoteUpdated}/-{NoteRemoved}) " +
            "regions(total={RegionCount} +{RegionAdded}/~{RegionUpdated}/-{RegionRemoved})",
            cause, sw.ElapsedMilliseconds, firstLoad,
            _routeById.Count, routeCounts.Added, routeCounts.Updated, routeCounts.Removed,
            _waypointById.Count, waypointCounts.Added, waypointCounts.Updated, waypointCounts.Removed,
            _noteById.Count, noteCounts.Added, noteCounts.Updated, noteCounts.Removed,
            _regionById.Count, regionCounts.Added, regionCounts.Updated, regionCounts.Removed);
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
    //
    // Reconcile flow per type: walk the fresh list, upsert into the
    // dictionary, fire Changed for each row. Then walk the dictionary
    // to find ids the server no longer has, remove them, fire Removed.
    // The shared HashSet field (_reconcileIdScratch) is the
    // upsert-side membership lookup; it's cleared rather than allocated
    // per call so a busy reconnect doesn't churn HashSet bucket arrays.
    // The stale-id list is allocated lazily -- on the common
    // no-stale-entries path we skip the List<> allocation entirely.

    private readonly HashSet<string> _reconcileIdScratch = new();

    private ReplaceCounts ReplaceRoutes(List<SignalkRoute> fresh)
    {
        var newIds = _reconcileIdScratch;
        newIds.Clear();
        int added = 0, updated = 0;
        bool mutated = false;
        foreach (var r in fresh)
        {
            if (string.IsNullOrEmpty(r.Id)) continue;
            newIds.Add(r.Id);
            if (_routeById.ContainsKey(r.Id)) updated++; else added++;
            _routeById[r.Id] = r;
            mutated = true;
            SafeInvoke(OnRouteChanged, r.Id, "route changed");
        }
        // Remove any cached entries no longer in the server's list.
        // Lazy-allocate the stale list so the no-stale common case
        // costs nothing beyond the keys foreach.
        List<string>? stale = null;
        foreach (var k in _routeById.Keys)
        {
            if (!newIds.Contains(k)) (stale ??= []).Add(k);
        }
        int removed = 0;
        if (stale is not null)
        {
            foreach (var id in stale)
            {
                _routeById.Remove(id);
                SafeInvoke(OnRouteRemoved, id, "route removed");
            }
            removed = stale.Count;
            mutated = true;
        }
        if (mutated) _routesSnapshot = null;
        return new ReplaceCounts(added, updated, removed);
    }

    private ReplaceCounts ReplaceWaypoints(List<SignalkWaypoint> fresh)
    {
        var newIds = _reconcileIdScratch;
        newIds.Clear();
        int added = 0, updated = 0;
        bool mutated = false;
        foreach (var w in fresh)
        {
            if (string.IsNullOrEmpty(w.Id)) continue;
            newIds.Add(w.Id);
            if (_waypointById.ContainsKey(w.Id)) updated++; else added++;
            _waypointById[w.Id] = w;
            mutated = true;
            SafeInvoke(OnWaypointChanged, w.Id, "waypoint changed");
        }
        List<string>? stale = null;
        foreach (var k in _waypointById.Keys)
        {
            if (!newIds.Contains(k)) (stale ??= []).Add(k);
        }
        int removed = 0;
        if (stale is not null)
        {
            foreach (var id in stale)
            {
                _waypointById.Remove(id);
                SafeInvoke(OnWaypointRemoved, id, "waypoint removed");
            }
            removed = stale.Count;
            mutated = true;
        }
        if (mutated) _waypointsSnapshot = null;
        return new ReplaceCounts(added, updated, removed);
    }

    private ReplaceCounts ReplaceNotes(List<SignalkNote> fresh)
    {
        var newIds = _reconcileIdScratch;
        newIds.Clear();
        int added = 0, updated = 0;
        bool mutated = false;
        foreach (var n in fresh)
        {
            if (string.IsNullOrEmpty(n.Id)) continue;
            newIds.Add(n.Id);
            if (_noteById.ContainsKey(n.Id)) updated++; else added++;
            _noteById[n.Id] = n;
            mutated = true;
            SafeInvoke(OnNoteChanged, n.Id, "note changed");
        }
        List<string>? stale = null;
        foreach (var k in _noteById.Keys)
        {
            if (!newIds.Contains(k)) (stale ??= []).Add(k);
        }
        int removed = 0;
        if (stale is not null)
        {
            foreach (var id in stale)
            {
                _noteById.Remove(id);
                SafeInvoke(OnNoteRemoved, id, "note removed");
            }
            removed = stale.Count;
            mutated = true;
        }
        if (mutated) _notesSnapshot = null;
        return new ReplaceCounts(added, updated, removed);
    }

    private ReplaceCounts ReplaceRegions(List<SignalkRegion> fresh)
    {
        var newIds = _reconcileIdScratch;
        newIds.Clear();
        int added = 0, updated = 0;
        bool mutated = false;
        foreach (var r in fresh)
        {
            if (string.IsNullOrEmpty(r.Id)) continue;
            newIds.Add(r.Id);
            if (_regionById.ContainsKey(r.Id)) updated++; else added++;
            _regionById[r.Id] = r;
            mutated = true;
            SafeInvoke(OnRegionChanged, r.Id, "region changed");
        }
        List<string>? stale = null;
        foreach (var k in _regionById.Keys)
        {
            if (!newIds.Contains(k)) (stale ??= []).Add(k);
        }
        int removed = 0;
        if (stale is not null)
        {
            foreach (var id in stale)
            {
                _regionById.Remove(id);
                SafeInvoke(OnRegionRemoved, id, "region removed");
            }
            removed = stale.Count;
            mutated = true;
        }
        if (mutated) _regionsSnapshot = null;
        return new ReplaceCounts(added, updated, removed);
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
                _routesSnapshot = null;
                SafeInvoke(OnRouteRemoved, id, "route removed");
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
            _routesSnapshot = null;
            SafeInvoke(OnRouteChanged, id, "route changed");
        }
        // Catch broadly: JsonException is the typed-deserialise failure,
        // but a malformed coordinates array can also surface as
        // InvalidOperationException / FormatException via downstream
        // GetDouble() in ParseRouteCoords (Map.razor consumes the
        // JsonElement). Without the broad catch, those propagate up
        // through DispatchResourceUpdates' own catch with no per-id
        // log, and the cache stays stale silently.
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
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
                _waypointsSnapshot = null;
                SafeInvoke(OnWaypointRemoved, id, "waypoint removed");
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
            _waypointsSnapshot = null;
            SafeInvoke(OnWaypointChanged, id, "waypoint changed");
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
                _notesSnapshot = null;
                SafeInvoke(OnNoteRemoved, id, "note removed");
            }
            return;
        }
        try
        {
            var note = value.Deserialize<SignalkNote>();
            if (note is null) return;
            // Mirror NoteApi.GetAllAsync's filter: notes without a
            // position are not renderable on the chart and would
            // confuse marker code that assumes lat/lon.
            if (note.Position is null) return;
            note.Id = id;
            _noteById[id] = note;
            _notesSnapshot = null;
            SafeInvoke(OnNoteChanged, id, "note changed");
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
                _regionsSnapshot = null;
                SafeInvoke(OnRegionRemoved, id, "region removed");
            }
            return;
        }
        try
        {
            var region = value.Deserialize<SignalkRegion>();
            if (region is null) return;
            region.Id = id;
            // Populate OuterRings the same way RegionApi.GetAllAsync does
            // for REST results -- consumers (Map.razor's region layer)
            // expect Leaflet-ordered [lat, lon] rings, not the GeoJSON
            // [lon, lat] coords on the wire. Without this hoist a
            // delta-fed region renders as an empty polygon on the chart.
            // Drop the region entirely when geometry is missing /
            // unrenderable, matching REST's filter.
            var coords = region.Feature?.Geometry?.Coordinates;
            var type = region.Feature?.Geometry?.Type;
            if (coords is not null)
            {
                region.OuterRings = OnaPlotter.Services.Api.RegionApi
                    .ExtractOuterRings(coords.Value, type);
            }
            if (region.OuterRings.Count == 0) return;
            _regionById[id] = region;
            _regionsSnapshot = null;
            SafeInvoke(OnRegionChanged, id, "region changed");
        }
        // Catch broadly: ExtractOuterRings -> ParseRing calls
        // coord[0].GetDouble() without first checking JsonValueKind.
        // Number, so a malformed coord (`[null, 50.0]`) throws
        // InvalidOperationException, not JsonException. A narrow catch
        // here would let that propagate up to DispatchResourceUpdates'
        // generic catch with no per-id log; the helm sees a silently
        // stale cache.
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _logger.LogWarning(ex, "[resources] region delta parse failed for id '{Id}'", id);
        }
    }

    // --- Reconnect reconcile -----------------------------------------

    private bool _wasConnected;

    /// <summary>The most recently kicked reconcile-on-reconnect Task,
    /// or <c>null</c> if none has fired yet. Exposed internally so
    /// tests can <c>await</c> the reconcile to completion before
    /// asserting on the post-reconcile cache state. Production never
    /// reads this -- the fire-and-forget runs on its own.</summary>
    internal Task? LastReconcileTask { get; private set; }

    private void HandleConnectionChange()
    {
        // The OnConnectionChanged event doesn't carry old/new state, so
        // we track edges ourselves: false -> true means "just (re)connected"
        // and is when we want to backfill any deltas missed during the
        // disconnect window via REST.
        var nowConnected = _signalk.IsConnected;
        var transitionedToConnected = !_wasConnected && nowConnected;
        var transitionedToDisconnected = _wasConnected && !nowConnected;
        _wasConnected = nowConnected;
        // Log both edges -- the disconnect log lets a helm reading the
        // journal correlate "lost connection at HH:MM:SS" with "deltas
        // stopped showing up", and the reconnect log paired with the
        // reconcile-complete line tells the same story for recovery.
        if (transitionedToDisconnected)
        {
            _logger.LogInformation("[resources] WS disconnected -- deltas paused, cache stale until reconnect");
        }
        if (!transitionedToConnected) return;
        _logger.LogInformation("[resources] WS reconnected -- kicking REST reconcile to backfill missed deltas");

        // Kick the reconcile and stash the Task on LastReconcileTask
        // so tests can await it.
        //
        // NOTE on threading: the synchronous prefix of
        // ReconcileOnReconnectAsync (the SafeFetch task allocations +
        // the first Task.WhenAll await) runs INLINE on whichever
        // thread fired OnConnectionChanged -- we no longer wrap in
        // Task.Run. On Blazor WASM that's a no-op (single-threaded);
        // on the WS-loop site (SignalkClient line 578) the pre-await
        // synchronous prefix is just task construction, so the loop
        // doesn't appreciably stall. The catch-all inside
        // ReconcileOnReconnectAsync still prevents an unobserved
        // exception from escaping into the OnConnectionChanged
        // invoker chain.
        LastReconcileTask = ReconcileOnReconnectAsync();
    }

    private async Task ReconcileOnReconnectAsync()
    {
        try { await RefreshAllAsync(cause: "reconnect"); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[resources] reconcile-on-reconnect failed");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _signalk.OnResourceDelta -= HandleResourceDelta;
        _signalk.OnConnectionChanged -= HandleConnectionChange;

        // Drain in-flight reconcile + refresh tasks so a page-unmount
        // (or app shutdown) doesn't leave async work mutating cache
        // dictionaries we've stopped notifying on. Both tasks have
        // their own internal try/catch; awaiting cannot throw at us
        // unless something genuinely escaped that net.
        var pending = new List<Task>(2);
        if (LastReconcileTask is { IsCompleted: false } reconcile) pending.Add(reconcile);
        if (_inFlightRefresh is { IsCompleted: false } refresh) pending.Add(refresh);
        if (pending.Count > 0)
        {
            try { await Task.WhenAll(pending); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[resources] in-flight task threw on dispose");
            }
        }
    }
}
