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
/// reconcile). Both pages that previously did their own REST polling -
/// <c>Map.razor</c> and <c>Resources.razor</c> - now read from this
/// store and subscribe to its typed change events.
///
/// <para><b>Why this exists:</b> the helm reported a multi-plotter
/// sync bug where editing a route on plotter A left plotter B's chart
/// stuck on the pre-edit geometry, even though the active-WP line
/// followed the new endpoint. Root cause: OnaPlotter never subscribed
/// to <c>resources.*</c> deltas, so the only refresh path was a local
/// re-fetch after the user's OWN save. The signalk-server emits
/// resource changes as ordinary deltas (full document on PUT/POST,
/// <c>null</c> on DELETE) - this store is the consumer that closes
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
///
/// <para><b>Architecture</b>: per-type cache + change-event behaviour
/// lives on <see cref="ResourceTypeCache{T}"/> - this class is the
/// composition layer that owns four instances (one per resource type),
/// dispatches WS deltas to the right cache, and orchestrates REST
/// reconcile across all four in parallel.</para>
/// </summary>
public sealed class ResourceStore : IAsyncDisposable
{
    private readonly IRouteApi _routesApi;
    private readonly IWaypointApi _waypointsApi;
    private readonly INoteApi _notesApi;
    private readonly IRegionApi _regionsApi;
    private readonly SignalkClient _signalk;
    private readonly ILogger<ResourceStore> _logger;

    // Per-type caches. Each owns its own dictionary, snapshot list,
    // Changed/Removed events, and the "log subscriber-throw" guard.
    // Blazor WASM is single-threaded so these don't need locks.
    // Shared HashSet for Replace-side membership scratch - one
    // allocation amortised across every reconcile pass.
    private readonly ResourceTypeCache<SignalkRoute> _routeCache;
    private readonly ResourceTypeCache<SignalkWaypoint> _waypointCache;
    private readonly ResourceTypeCache<SignalkNote> _noteCache;
    private readonly ResourceTypeCache<SignalkRegion> _regionCache;
    private readonly HashSet<string> _reconcileIdScratch = new();

    /// <summary>Coalesces concurrent <see cref="RefreshAllAsync"/>
    /// calls. Two callers (manual Refresh button + reconnect-edge
    /// reconcile) can land at the same moment on a flaky link; without
    /// the gate, two passes interleave their cache mutations and fire
    /// 2x the per-row Changed events. With it, the second caller
    /// awaits the first instead of re-issuing four REST round-trips.</summary>
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
        _routesApi = routes;
        _waypointsApi = waypoints;
        _notesApi = notes;
        _regionsApi = regions;
        _signalk = signalk;
        _logger = logger;

        _routeCache = new ResourceTypeCache<SignalkRoute>("route", logger);
        _waypointCache = new ResourceTypeCache<SignalkWaypoint>("waypoint", logger);
        _noteCache = new ResourceTypeCache<SignalkNote>("note", logger);
        _regionCache = new ResourceTypeCache<SignalkRegion>("region", logger);

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
    public IReadOnlyList<SignalkRoute> Routes => _routeCache.Snapshot;

    public IReadOnlyList<SignalkWaypoint> Waypoints => _waypointCache.Snapshot;

    public IReadOnlyList<SignalkNote> Notes => _noteCache.Snapshot;

    public IReadOnlyList<SignalkRegion> Regions => _regionCache.Snapshot;

    public SignalkRoute? GetRoute(string id) => _routeCache.Get(id);

    public SignalkWaypoint? GetWaypoint(string id) => _waypointCache.Get(id);

    public SignalkNote? GetNote(string id) => _noteCache.Get(id);

    public SignalkRegion? GetRegion(string id) => _regionCache.Get(id);

    // --- Change events (per type) ------------------------------------
    //
    // Subscribers must handle thread-affinity themselves (Blazor
    // components: InvokeAsync(StateHasChanged) wrap). Removed events
    // fire AFTER the entry is dropped from the cache, so a handler
    // that re-checks via GetRoute(id) gets null. Custom event
    // accessors forward to the inner cache so the public surface
    // stays Action<string>?-shaped (existing subscribers don't need
    // to change).

    /// <summary>Fires when a route is created or updated. Argument is
    /// the resource id; the new document is in the cache when this
    /// fires (look it up via <see cref="GetRoute"/>).</summary>
    public event Action<string>? OnRouteChanged
    {
        add { _routeCache.Changed += value; }
        remove { _routeCache.Changed -= value; }
    }

    /// <summary>Fires when a route is deleted (delta value=null OR a
    /// REST reconcile finds the id is no longer in the server's
    /// list). Cache entry is already gone when this fires.</summary>
    public event Action<string>? OnRouteRemoved
    {
        add { _routeCache.Removed += value; }
        remove { _routeCache.Removed -= value; }
    }

    public event Action<string>? OnWaypointChanged
    {
        add { _waypointCache.Changed += value; }
        remove { _waypointCache.Changed -= value; }
    }

    public event Action<string>? OnWaypointRemoved
    {
        add { _waypointCache.Removed += value; }
        remove { _waypointCache.Removed -= value; }
    }

    public event Action<string>? OnNoteChanged
    {
        add { _noteCache.Changed += value; }
        remove { _noteCache.Changed -= value; }
    }

    public event Action<string>? OnNoteRemoved
    {
        add { _noteCache.Removed += value; }
        remove { _noteCache.Removed -= value; }
    }

    public event Action<string>? OnRegionChanged
    {
        add { _regionCache.Changed += value; }
        remove { _regionCache.Changed -= value; }
    }

    public event Action<string>? OnRegionRemoved
    {
        add { _regionCache.Removed += value; }
        remove { _regionCache.Removed -= value; }
    }

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
    ///
    /// <param name="cause">Free-form tag describing why the reconcile
    /// fired - "startup", "reconnect", "page-mount", "manual",
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

    private static readonly ResourceTypeCache<SignalkRoute>.ReplaceCounts ZeroCounts = default;

    private async Task RefreshAllCoreAsync(string cause, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("[resources] reconcile started cause={Cause}", cause);

        var routesTask = SafeFetch(() => _routesApi.GetAllAsync(ct), "routes");
        var waypointsTask = SafeFetch(() => _waypointsApi.GetAllAsync(ct), "waypoints");
        var notesTask = SafeFetch(() => _notesApi.GetAllAsync(ct), "notes");
        var regionsTask = SafeFetch(() => _regionsApi.GetAllAsync(ct), "regions");
        await Task.WhenAll(routesTask, waypointsTask, notesTask, regionsTask);

        var routeCounts = routesTask.Result is { } routes
            ? _routeCache.Replace(routes, r => r.Id, _reconcileIdScratch)
            : ZeroCounts;
        var waypointCounts = waypointsTask.Result is { } waypoints
            ? _waypointCache.Replace(waypoints, w => w.Id, _reconcileIdScratch)
            : default;
        var noteCounts = notesTask.Result is { } notes
            ? _noteCache.Replace(notes, n => n.Id, _reconcileIdScratch)
            : default;
        var regionCounts = regionsTask.Result is { } regions
            ? _regionCache.Replace(regions, r => r.Id, _reconcileIdScratch)
            : default;

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
            _routeCache.Count, routeCounts.Added, routeCounts.Updated, routeCounts.Removed,
            _waypointCache.Count, waypointCounts.Added, waypointCounts.Updated, waypointCounts.Removed,
            _noteCache.Count, noteCounts.Added, noteCounts.Updated, noteCounts.Removed,
            _regionCache.Count, regionCounts.Added, regionCounts.Updated, regionCounts.Removed);
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
            _routeCache.Remove(id);
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
            _routeCache.Apply(id, route);
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
            _waypointCache.Remove(id);
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
            _waypointCache.Apply(id, wp);
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
            _noteCache.Remove(id);
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
            _noteCache.Apply(id, note);
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
            _regionCache.Remove(id);
            return;
        }
        try
        {
            var region = value.Deserialize<SignalkRegion>();
            if (region is null) return;
            region.Id = id;
            // Populate OuterRings the same way RegionApi.GetAllAsync does
            // for REST results - consumers (Map.razor's region layer)
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
            _regionCache.Apply(id, region);
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
    /// reads this - the fire-and-forget runs on its own.</summary>
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
        // Log both edges - the disconnect log lets a helm reading the
        // journal correlate "lost connection at HH:MM:SS" with "deltas
        // stopped showing up", and the reconnect log paired with the
        // reconcile-complete line tells the same story for recovery.
        if (transitionedToDisconnected)
        {
            _logger.LogInformation("[resources] WS disconnected - deltas paused, cache stale until reconnect");
        }
        if (!transitionedToConnected) return;
        _logger.LogInformation("[resources] WS reconnected - kicking REST reconcile to backfill missed deltas");

        // Kick the reconcile and stash the Task on LastReconcileTask
        // so tests can await it.
        //
        // NOTE on threading: the synchronous prefix of
        // ReconcileOnReconnectAsync (the SafeFetch task allocations +
        // the first Task.WhenAll await) runs INLINE on whichever
        // thread fired OnConnectionChanged - we no longer wrap in
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
