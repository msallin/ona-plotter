using System.Text.Json;
using Microsoft.Extensions.Logging;
using OnaPlotter.Models;
using OnaPlotter.Services.Api;
using OnaPlotter.Services.ServerNotifications;

namespace OnaPlotter.Services.Mob;

/// <summary>
/// Default <see cref="IMobService"/>. Drives a local-first MOB
/// pipeline: synthesise into <see cref="ServerNotificationStore"/>,
/// then reconcile against the server via REST + WS echo.
///
/// <para>Single source of truth for "what MOBs are active": the
/// store. MobService keeps a small auxiliary map of pending raises
/// (localId -> persisted PendingRaise + retry timer) so the
/// reconciliation step knows which entries are local-only and which
/// have a serverId. After REST success the local synthetic is
/// removed; the server's path is the canonical entry.</para>
///
/// <para>Design alternative considered + rejected: rewrite the
/// store path-key from notifications.mob.&lt;localId&gt; to
/// notifications.mob.&lt;serverId&gt; in place on REST success, so
/// the WS echo lands as an idempotent re-apply on an entry that's
/// already there. Removes the dual-entry window but requires the
/// store to grow a "rename path" primitive that doesn't fit the
/// path-keyed dict shape and that the WS-driven Apply path doesn't
/// need. The current dual-entry-with-reconciliation approach keeps
/// the store API minimal at the cost of one extra Clear per
/// successful raise - acceptable.</para>
///
/// <para>Persistence: the pending-raise queue is written to
/// localStorage (key <c>mob.pendingRaise.v1</c>) so an offline
/// emit survives a reload. On <see cref="InitializeAsync"/>, the
/// queue is replayed and the server's active list is pulled to
/// recover MOBs raised before this plotter was around.</para>
///
/// <para><b>Offline-resilient paired waypoint</b>: the persistent
/// MOB chart pin (the waypoint composed alongside the notification)
/// runs through its own retry loop using the same backoff schedule
/// as the notification side. The waypoint id is generated client-
/// side as a Guid and the PUT goes to
/// <c>/resources/waypoints/{id}</c> - SK v2 resources-api accepts
/// PUT-on-not-yet-existing as create, so a retry that lands after
/// the server already received the first attempt just overwrites
/// in place rather than minting a duplicate pin (POST
/// <c>/resources/waypoints</c> would mint a fresh id per call,
/// leaving N duplicate pins after N retries). The pending-raise
/// record carries <see cref="PendingRaise.WaypointPostPending"/> +
/// <see cref="PendingRaise.WaypointId"/> so a reload mid-retry
/// resumes the loop on the next session via
/// <see cref="InitializeAsync"/>. Helm sees one toast on the first
/// failure ("MOB chart pin queued (offline)...") and no further
/// noise; the chart marker appears as soon as the PUT lands.</para>
/// </summary>
public sealed class MobService : IMobService, IDisposable
{
    private const string StorageKey = "mob.pendingRaise.v1";
    private const string MobPathPrefix = "notifications.mob.";

    /// <summary>Default banner copy when the helm doesn't pass an
    /// explicit message. Single source of truth - Map.razor passes
    /// null and inherits this so a future i18n / wording change has
    /// one site to edit.</summary>
    public const string DefaultRaiseMessage = "Person Overboard!";

    /// <summary>Backoff schedule for the REST retry loop. After
    /// the table is exhausted the service falls back to the last
    /// entry indefinitely - per spec, "never hide or block the
    /// alarm because of network failure".</summary>
    internal static readonly TimeSpan[] BackoffSchedule =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
    ];

    private readonly INotificationsApi _api;
    private readonly ServerNotificationStore _store;
    private readonly IKeyValueStore _kv;
    private readonly ResolvedPositionStore _resolvedPositions;
    private readonly TimeProvider _time;
    private readonly ILogger<MobService> _logger;
    /// <summary>Optional waypoint composer. When wired, every
    /// <see cref="RaiseAsync"/> creates a paired waypoint with
    /// <c>isMob: true</c> + <c>isActive: true</c> + the local
    /// notification id stored in <c>mobAlarmId</c>. <see cref="ClearAsync"/>
    /// flips the waypoint to <c>isActive: false</c> instead of
    /// deleting it - the waypoint stays as a persistent MOB
    /// history. Null in test ctors that don't exercise the
    /// waypoint path (legacy 5-arg ctor); production DI always
    /// wires it.</summary>
    private readonly OnaPlotter.Services.Api.IWaypointApi? _waypoints;
    /// <summary>Optional waypoint-cache reader, paired with
    /// <see cref="_waypoints"/>. Used by <see cref="ClearAsync"/>
    /// to look up the existing MOB waypoint by id (so the PUT
    /// preserves coords / createdAt / name) when the pending-raise
    /// map's <c>WaypointId</c> field has been cleared (e.g. the
    /// notification was raised in a previous session and only the
    /// resource cache survived). Narrowed to <see cref="OnaPlotter.Services.Resources.IWaypointReader"/>
    /// rather than the concrete <c>ResourceStore</c> so unit tests
    /// can supply a list-backed fake without standing up the full
    /// HTTP + WS + dedup pipeline. Null when <see cref="_waypoints"/>
    /// is null.</summary>
    private readonly OnaPlotter.Services.Resources.IWaypointReader? _resources;

    /// <summary>Helm-facing failure surface for the waypoint side of
    /// the MOB pipeline. Invoked when a paired-waypoint create / PUT
    /// fails so the helm sees a toast like "MOB chart pin failed to
    /// save - alarm still active" rather than the failure dissolving
    /// into the WASM console. Optional: tests pass null and just
    /// inspect the logger output. The notification side is unaffected;
    /// this only narrates the waypoint-half of the safety contract.</summary>
    private readonly Action<string>? _toastWarning;

    /// <summary>Live pending raises keyed by localId. One entry per
    /// in-flight MOB; bundles the persistable state, the cancel
    /// handle, and the retry-loop task so add/remove is a single
    /// dictionary operation - no chance of leaving an orphan CTS
    /// or task behind. The persistable <see cref="PendingRaise"/>
    /// inside survives reload via localStorage; the cancel handle
    /// + loop task are session-scoped.</summary>
    private readonly Dictionary<string, LivePending> _pending =
        new(StringComparer.Ordinal);

    private bool _disposed;

    /// <summary>Optional WS client. When wired, the service subscribes
    /// to <see cref="SignalkClient.OnConnectionChanged"/> so a reconnect
    /// edge triggers <see cref="ReconcileFromServerAsync"/> automatically
    /// (cross-plotter MOB raised during a disconnect window lands on
    /// this plotter's banner without the helm having to navigate to the
    /// Map page). Null in legacy test ctors that don't exercise the
    /// connection-edge path; production DI always wires it.</summary>
    private readonly SignalkClient? _signalk;
    private bool _wasConnected;

    /// <summary>Last reconcile-on-reconnect Task (or null if none has
    /// fired yet). Exposed internally so tests can <c>await</c> the
    /// reconcile to completion before asserting on the post-reconcile
    /// store state. Production never reads this - the fire-and-forget
    /// runs on its own.</summary>
    internal Task? LastReconcileTask { get; private set; }

    /// <summary>Idempotency guard for <see cref="LoadSessionStateAsync"/>.
    /// Set to true the first time <see cref="InitializeAsync"/> kicks
    /// the session-init block; subsequent calls (e.g. the connect-edge
    /// reconcile, a manual replay from a test, or a stray re-init from
    /// a page mount under the old wiring) skip the persisted-pending
    /// reload + retry-loop spawn to avoid duplicating the in-flight
    /// retry tasks. The reconcile-from-server portion is NOT guarded:
    /// every connect-edge re-pulls so notifications raised during the
    /// disconnect window land on the local store.</summary>
    private bool _sessionLoaded;

    public MobService(
        INotificationsApi api,
        ServerNotificationStore store,
        IKeyValueStore kv,
        ResolvedPositionStore resolvedPositions,
        TimeProvider time,
        ILogger<MobService>? logger = null,
        OnaPlotter.Services.Api.IWaypointApi? waypoints = null,
        OnaPlotter.Services.Resources.IWaypointReader? resources = null,
        Action<string>? toastWarning = null,
        SignalkClient? signalk = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _kv = kv ?? throw new ArgumentNullException(nameof(kv));
        _resolvedPositions = resolvedPositions ?? throw new ArgumentNullException(nameof(resolvedPositions));
        _time = time ?? TimeProvider.System;
        // Logger is optional so the existing test ctor (no DI host)
        // still works - the retry-loop crash logs route through this
        // logger in production but a NullLogger keeps unit tests green.
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MobService>.Instance;
        // Waypoints + ResourceStore are optional: legacy test ctors
        // exercise notification-only flows. Production DI always
        // wires both (see Program.cs).
        _waypoints = waypoints;
        _resources = resources;
        _toastWarning = toastWarning;
        _signalk = signalk;

        // Reconciliation: when any notifications.mob.* path mutates in
        // the store - WS echo from the server, our own synthetic, or
        // a clear - ReconcileMobPath checks if a pending raise's
        // recorded serverId matches the path and, if so, tears down
        // the local synthetic so the helm sees one banner / one marker.
        // Also serves the alarm-pipeline wake-up: MainLayout
        // subscribes to the same OnPathChanged and re-evaluates, so
        // the synthetic path triggers banner + audio without a
        // dedicated callback.
        _store.OnPathChanged += ReconcileMobPath;

        // Wire WS connection-edge reconcile when the SignalkClient is
        // available. Previous wiring routed this through Map.razor's
        // HandleConnectionChanged, which meant MOB cross-reload recovery
        // only ran when the helm landed on the chart page. Owning it
        // here decouples MOB safety from page lifecycle - a reload on
        // Wind / Settings / History still recovers an in-flight MOB.
        if (_signalk is not null)
        {
            _wasConnected = _signalk.IsConnected;
            _signalk.OnConnectionChanged += HandleConnectionChanged;
        }
    }

    /// <summary>WS reconnect-edge handler. Tracks the
    /// disconnected→connected transition because <see
    /// cref="SignalkClient.OnConnectionChanged"/> fires on both edges
    /// and only the connect-up edge needs to re-pull the server's
    /// active notification list. Fires the reconcile in the background
    /// so the SignalkClient's connect-up dispatch isn't stalled on a
    /// slow REST + localStorage round-trip.</summary>
    private void HandleConnectionChanged()
    {
        if (_signalk is null || _disposed) return;
        bool nowConnected = _signalk.IsConnected;
        bool transitionedToConnected = !_wasConnected && nowConnected;
        _wasConnected = nowConnected;
        if (!transitionedToConnected) return;
        LastReconcileTask = ReconcileOnReconnectAsync();
    }

    private async Task ReconcileOnReconnectAsync()
    {
        try { await ReconcileFromServerAsync(CancellationToken.None); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[mob] reconcile-on-reconnect failed");
        }
    }

    /// <inheritdoc/>
    public async Task<string> RaiseAsync(
        string? message, double? latitude, double? longitude,
        CancellationToken ct = default)
    {
        var localId = Guid.NewGuid().ToString();
        var msg = string.IsNullOrWhiteSpace(message) ? DefaultRaiseMessage : message;
        var path = MobPathPrefix + localId;
        var status = new NotificationStatus(
            Silenced: false,
            Acknowledged: false,
            CanSilence: false,    // emergency state -> server pins this false
            CanAcknowledge: true,
            CanClear: true);

        // Inject the synthetic notification BEFORE the REST call so
        // the alarm banner + chart marker fire even when the
        // network is down. The same store path is what the WS echo
        // would land at; we drop it on REST success so the
        // server's canonical entry can take over.
        bool stored = _store.Apply(path, "emergency", msg, localId, status, latitude, longitude);
        // OnPathChanged fires synchronously inside Apply; the alarm-
        // pipeline subscriber wakes the banner + audio pipeline
        // without a separate callback (the same way a WS-driven
        // delta would).

        // Track + persist the pending raise so a reload mid-retry
        // resumes where it left off.
        var raise = new PendingRaise(
            LocalId: localId,
            Message: msg,
            Latitude: latitude,
            Longitude: longitude,
            AttemptCount: 0);
        var live = new LivePending(raise, new CancellationTokenSource());
        _pending[localId] = live;
        await PersistPendingAsync(ct);

        // Pair the alarm with a persistent MOB waypoint. Best-effort:
        // alarm path is independent of the waypoint - a waypoint POST
        // failure (offline, server reject, ...) does NOT abort the
        // notification side. Stored on LivePending so the retry-loop's
        // success path can await it before firing UpdateMobAlarmIdAsync;
        // assigned BEFORE Task.Run schedules the loop so the loop's
        // threadpool task can never observe a null CreateTask.
        // Skipped when no position is available (the waypoint needs
        // lat/lon) or when the test ctor didn't wire WaypointApi.
        if (_waypoints is not null && latitude is double mobLat && longitude is double mobLon)
        {
            // Cancellation token piggy-backs the loop's CTS so a
            // Service.Dispose / ClearAsync teardown unwinds a parked
            // HTTP POST instead of leaking a Task that holds the test
            // runner alive past process exit (a TaskCompletionSource
            // park has no GC affinity but the test framework's
            // shutdown coordination waits on outstanding work).
            live.CreateTask = CreateMobWaypointAsync(localId, mobLat, mobLon, live.Cts.Token);
        }

        // Background retry. The CTS is the loop's exit signal
        // (echo arrival, explicit cancel, dispose). The Task is
        // captured so tests can await it - production callers
        // ignore the handle.
        live.Loop = Task.Run(() => RunRaiseLoopAsync(raise, live.Cts.Token));

        return localId;
    }

    /// <summary>Create the MOB-paired waypoint. Self-retrying with
    /// the same backoff schedule as the notification side so an
    /// offline raise doesn't lose the chart pin: when the network
    /// returns the loop's next iteration succeeds and the marker
    /// appears (no need for the helm to manually re-raise).
    ///
    /// <para>Idempotency: client-generates the waypoint id (a Guid
    /// distinct from the localId, scoped to the resource namespace)
    /// up front and PUTs to <c>/resources/waypoints/{id}</c>; SK v2
    /// resources-api accepts PUT-on-not-yet-existing as create, so a
    /// retry that lands after the server already received the first
    /// attempt just overwrites in place rather than minting a duplicate
    /// pin. Persisted on <see cref="PendingRaise.WaypointId"/> +
    /// <see cref="PendingRaise.WaypointPostPending"/> so a reload
    /// mid-retry resumes the loop on the next session via
    /// <see cref="InitializeAsync"/>.</para>
    ///
    /// <para>Returns the in-memory <see cref="SignalkWaypoint"/> once
    /// the PUT lands; null on cancellation or no-waypoints DI.</para></summary>
    private async Task<SignalkWaypoint?> CreateMobWaypointAsync(string localId, double lat, double lon, CancellationToken ct = default)
    {
        if (_waypoints is null) return null;

        // Resolve / persist the waypoint id + name up front so a
        // cancel mid-retry leaves a replayable record. On the first
        // call we generate a fresh Guid; on InitializeAsync replay
        // the values are already populated and we re-use them.
        string waypointId;
        string name;
        if (_pending.TryGetValue(localId, out var live0)
            && live0.Raise.WaypointId is { Length: > 0 } existingId
            && live0.Raise.WaypointName is { Length: > 0 } existingName)
        {
            // Replay path: keep the same id (idempotent PUT) and
            // the same helm-time-of-drop name so the chart label
            // matches what the helm originally saw.
            waypointId = existingId;
            name = existingName;
        }
        else
        {
            waypointId = Guid.NewGuid().ToString();
            // Local time (helm clock) so the helm reads "MOB: 14:32:07"
            // matching their watch. Routed via TimeProvider.GetLocalNow()
            // so unit tests can pin a non-UTC offset; .DateTime gives
            // the wall-clock face at the offset (not reinterpreted via
            // the system clock).
            name = FormattableString.Invariant(
                $"MOB: {_time.GetLocalNow().DateTime:HH:mm:ss}");
        }

        // Stamp the id + WaypointPostPending on the pending raise
        // BEFORE the first PUT attempt so a process crash between
        // here and a successful response doesn't lose the id - the
        // next session's replay re-uses the same id and idempotent-
        // PUTs over the (possibly already-created) server resource.
        if (_pending.TryGetValue(localId, out var liveSeed))
        {
            liveSeed.Raise = liveSeed.Raise with
            {
                WaypointId = waypointId,
                WaypointName = name,
                WaypointPostPending = true,
            };
            await PersistPendingAsync(CancellationToken.None);
        }

        // Build the in-memory wp once - same instance returned on
        // success and stamped on live.CreatedWaypoint so ClearAsync's
        // tier-1 fast path finds it without a cache lookup.
        var wp = new OnaPlotter.Models.SignalkWaypoint
        {
            Id = waypointId,
            Name = name,
            Description = null,
            Latitude = lat,
            Longitude = lon,
            CreatedAt = _time.GetUtcNow().UtcDateTime,
            IsMob = true,
            IsMobActive = true,
            MobAlarmId = localId,
        };

        // Backoff loop. Same schedule as RunRaiseLoopCoreAsync so the
        // helm sees the alarm + chart pin land at consistent moments
        // when offline-raise reconnects (1s, 2s, 5s, 10s, 30s, then
        // 60s steady-state). First attempt is immediate.
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            // Honour helm-clear-while-still-creating: if ClearAsync
            // marked ClearRequested, we don't need to bother the
            // server with this PUT - the deferred-clear path will
            // wipe both sides. Bailing here also keeps the server
            // history clean for an offline-MOB the helm aborted
            // before reconnect.
            if (_pending.TryGetValue(localId, out var liveCheck)
                && liveCheck.Raise.ClearRequested)
            {
                return null;
            }

            if (attempt > 0)
            {
                int idx = Math.Min(attempt - 1, BackoffSchedule.Length - 1);
                try { await Task.Delay(BackoffSchedule[idx], _time, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return null; }
            }

            ApiResult result;
            try
            {
                result = await _waypoints.PutWithIdAsync(
                    waypointId, name, lat, lon, description: null,
                    isMob: true, isActive: true, mobAlarmId: localId, ct: ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[mob] waypoint PUT threw for {WpId}; will retry", waypointId);
                attempt++;
                continue;
            }

            if (result.Success)
            {
                _logger.LogInformation(
                    "[mob] waypoint pinned {WaypointId} for {LocalId} (attempt {Attempt})",
                    waypointId, localId, attempt);
                if (_pending.TryGetValue(localId, out var liveOk))
                {
                    liveOk.Raise = liveOk.Raise with { WaypointPostPending = false };
                    liveOk.CreatedWaypoint = wp;
                    await PersistPendingAsync(CancellationToken.None);
                }
                return wp;
            }

            // Failure: log every attempt at warning level, but only
            // toast once (on the first attempt) so a long offline
            // window doesn't spam the helm with "still trying" toasts.
            // The retry continues silently; on success the info-log
            // confirms the pin landed.
            _logger.LogWarning(
                "[mob] waypoint PUT failed for {WpId} (attempt {Attempt}): {Error}",
                waypointId, attempt, result.Error ?? "(no body)");
            if (attempt == 0)
            {
                _toastWarning?.Invoke(
                    $"MOB chart pin queued (offline): {result.Error ?? "server unreachable"} - alarm still active, will retry");
            }
            attempt++;
        }
        return null;
    }

    /// <summary>Update the MOB waypoint's <c>mobAlarmId</c> from the
    /// localId to the server-assigned notification id. Called from
    /// the notification retry loop's success path so any plotter
    /// observing <c>notifications.mob.&lt;serverId&gt;</c> can
    /// correlate the waypoint by mobAlarmId. Takes the waypoint
    /// instance by reference (stamped on <see cref="LivePending.CreatedWaypoint"/>
    /// at create-time) rather than looking it up via id - the WS echo
    /// of the just-created waypoint may not have landed in the resource
    /// cache yet, and a cache lookup race would silently no-op leaving
    /// the wrong mobAlarmId on the wire.</summary>
    private async Task UpdateMobAlarmIdAsync(SignalkWaypoint wp, string newAlarmId)
    {
        if (_waypoints is null) return;
        try
        {
            // Preserve all current fields; only mobAlarmId changes.
            var result = await _waypoints.UpdateAsync(
                wp, wp.Name ?? "", wp.Description,
                isMob: true, isActive: wp.IsMobActive, mobAlarmId: newAlarmId);
            if (!result.Success)
            {
                _logger.LogWarning(
                    "[mob] waypoint mobAlarmId update failed for {WpId}: {Error}",
                    wp.Id, result.Error ?? "(no body)");
                _toastWarning?.Invoke($"MOB cross-plotter correlation failed - other plotters may not find this MOB by its server id");
                return;
            }
            // Mirror the new id on the in-memory copy so a subsequent
            // SetMobInactiveAsync (helm clears immediately) PUTs the
            // correct mobAlarmId.
            wp.MobAlarmId = newAlarmId;
            _logger.LogInformation(
                "[mob] waypoint mobAlarmId updated {WpId} -> {ServerId}",
                wp.Id, newAlarmId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[mob] waypoint mobAlarmId update threw for {WpId}", wp.Id);
            _toastWarning?.Invoke($"MOB cross-plotter correlation failed: {ex.Message}");
        }
    }

    /// <summary>Flip the MOB waypoint's <c>isActive</c> to false
    /// without deleting it. Called from <see cref="ClearAsync"/> so
    /// the waypoint stays on the chart (and in the Resources list)
    /// as a persistent history of past MOBs - the helm can revisit
    /// where a casualty was raised even after the alarm cleared.</summary>
    private async Task SetMobInactiveAsync(SignalkWaypoint wp)
    {
        if (_waypoints is null) return;
        try
        {
            var result = await _waypoints.UpdateAsync(
                wp, wp.Name ?? "", wp.Description,
                isMob: true, isActive: false, mobAlarmId: wp.MobAlarmId);
            if (!result.Success)
            {
                _logger.LogWarning(
                    "[mob] waypoint deactivate failed for {WpId}: {Error}",
                    wp.Id, result.Error ?? "(no body)");
                _toastWarning?.Invoke($"MOB cleared but chart marker still active - server unreachable");
                return;
            }
            _logger.LogInformation(
                "[mob] waypoint deactivated {WpId}",
                wp.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[mob] waypoint deactivate threw for {WpId}", wp.Id);
            _toastWarning?.Invoke($"MOB cleared but chart marker still active: {ex.Message}");
        }
    }

    /// <summary>Locate the MOB waypoint paired with a notification
    /// id. Three-tier lookup:
    /// <list type="number">
    /// <item>In-memory <see cref="LivePending.CreatedWaypoint"/>
    /// stamped at create-time. Cheapest and dodges the resource-cache
    /// race where the WS echo of the freshly-created waypoint has not
    /// landed yet. Available on a same-session clear.</item>
    /// <item>Pending-raise map by <see cref="PendingRaise.WaypointId"/>
    /// + resource-cache lookup. Same-session clear, but rare path
    /// after the in-memory tier is in place.</item>
    /// <item>Scan over <see cref="ResourceStore.Waypoints"/> by
    /// <c>MobAlarmId</c>. Covers the cleared-after-reload case where
    /// the pending entry has been retired and only the resource cache
    /// survived.</item>
    /// </list>
    /// The scan is bounded by the typical waypoint count and only
    /// runs on user-initiated <see cref="ClearAsync"/>, so the cost
    /// is trivial.</summary>
    private SignalkWaypoint? FindMobWaypoint(string id, string? matchedLocalId)
    {
        // Tier 1: in-memory waypoint stamped at create-time.
        if (matchedLocalId is not null
            && _pending.TryGetValue(matchedLocalId, out var live))
        {
            if (live.CreatedWaypoint is { } createdWp) return createdWp;
            // Tier 2: cache lookup via stamped id.
            if (live.Raise.WaypointId is { Length: > 0 } wpId
                && _resources?.GetWaypoint(wpId) is { } cachedWp)
            {
                return cachedWp;
            }
        }
        // Tier 3: scan by mobAlarmId. Matches either the passed-in id
        // (could be localId or serverId) OR the resolved matchedLocalId
        // (covers the early-clear case where the notification's serverId
        // arrived but the waypoint still carries the localId).
        if (_resources is null) return null;
        foreach (var w in _resources.Waypoints)
        {
            if (!w.IsMob) continue;
            if (w.MobAlarmId == id) return w;
            if (matchedLocalId is not null && w.MobAlarmId == matchedLocalId) return w;
        }
        return null;
    }

    /// <inheritdoc/>
    public async Task<bool> ClearAsync(string id, CancellationToken ct = default)
    {
        // Resolve "is this a pending local id (no server twin yet)
        // or an already-synced server id?" If a pending raise's
        // localId matches OR its serverId matches, cancel the retry
        // loop first - otherwise the loop completes a successful
        // POST after the helm has cleared, and the WS echo of the
        // server twin resurrects the MOB.
        string? matchedLocalId = null;
        bool hasServerSide = false;
        foreach (var (localId, live) in _pending)
        {
            if (string.Equals(localId, id, StringComparison.Ordinal))
            {
                matchedLocalId = localId;
                hasServerSide = live.Raise.ServerId is not null;
                break;
            }
            if (live.Raise.ServerId is { } sid && string.Equals(sid, id, StringComparison.Ordinal))
            {
                matchedLocalId = localId;
                hasServerSide = true;
                break;
            }
        }
        // Resolve the paired MOB waypoint BEFORE CancelAndDropPending
        // tears the LivePending entry down. FindMobWaypoint's tier-1
        // fast path reads live.CreatedWaypoint off the pending entry;
        // dropping the entry first would force every clear into the
        // tier-3 scan and break the same-session deactivate path when
        // _resources is null.
        SignalkWaypoint? mobWp = _waypoints is not null
            ? FindMobWaypoint(id, matchedLocalId)
            : null;

        // PARA-003: race-window zombie. When the helm clears while
        // the notification POST is still in flight, the loop hasn't
        // recorded a serverId yet (hasServerSide=false). If we
        // cancelled the loop now, the in-flight POST may still land
        // server-side: the server creates the notification and the
        // WS echo arrives at notifications.mob.<serverId> with no
        // matching pending entry - ReconcileMobPath returns early
        // and the server-twin sits in _store.Active forever (mobActive
        // flips back to TRUE after the helm thought they cleared it).
        // Mitigation: stamp ClearRequested on the pending raise + let
        // the loop run. The loop's success branch sees the flag,
        // fires DELETE on the just-assigned serverId, and exits. We
        // still drop the local synthetic + the chart waypoint NOW so
        // the helm's UX is "clear was instantaneous"; the server-side
        // cleanup happens in the background.
        bool deferredClear = matchedLocalId is not null && !hasServerSide;
        if (deferredClear)
        {
            // Stamp the flag in-place; the loop reads it after each
            // RaiseMobAsync call. Persist so a reload mid-race
            // resumes the cleanup. DON'T CancelAndDropPending here -
            // that would kill the loop before it can DELETE.
            if (_pending.TryGetValue(matchedLocalId!, out var liveDeferred))
            {
                liveDeferred.Raise = liveDeferred.Raise with { ClearRequested = true };
                await PersistPendingAsync(ct).ConfigureAwait(false);
            }
        }
        else if (matchedLocalId is not null)
        {
            CancelAndDropPending(matchedLocalId);
        }

        // Drop the local synthetic at notifications.mob.<localId>
        // (if we matched one) AND the path the helm passed (could
        // be the server twin path notifications.mob.<serverId>).
        // Safe-no-ops on absent paths. Skip the second Clear when
        // the helm-passed id resolved to the same localId we just
        // cleared - one Clear, no rereading the dispatcher.
        if (matchedLocalId is not null)
        {
            _store.Clear(MobPathPrefix + matchedLocalId);
        }
        if (matchedLocalId is null || !string.Equals(matchedLocalId, id, StringComparison.Ordinal))
        {
            _store.Clear(MobPathPrefix + id);
        }

        // Drop the resolved-position cache entry for this MOB so a
        // future raise of an unrelated MOB doesn't pick up a stale
        // fix from a long-resolved casualty.
        _resolvedPositions.Forget(id);

        // Flip the paired MOB waypoint to isActive=false so the
        // chart stops pulsing but the waypoint stays as a
        // persistent MOB history entry. Best-effort - a failed
        // PUT logs + toasts but doesn't abort the notification clear
        // (a server outage or a stale resource cache shouldn't keep
        // the audible alarm armed).
        if (mobWp is not null)
        {
            _ = SetMobInactiveAsync(mobWp);
        }

        // Skip the REST call when we know there's no server-side
        // entry to clear. Calling DELETE /<localId> would 404 (the
        // server never saw that id) and the call wastes a round-
        // trip + can confuse a future observer in the server log.
        // Also skip in the deferred-clear case: the retry loop's
        // success branch will issue DELETE on the assigned serverId
        // once the in-flight POST returns.
        if (matchedLocalId is not null && !hasServerSide)
        {
            return true;
        }

        // DELETE /signalk/v2/api/notifications/<id> - the actual
        // clear path on signalk-server. The previous POST .../clear
        // endpoint returned 404 (it isn't routed), so the local
        // plotter cleared its own banner but no WS delta ever
        // broadcast and other plotters' banners stayed up. The
        // server's DELETE handler emits the "normal"/"cleared"
        // notifications delta we rely on for cross-plotter sync.
        var result = await _api.ClearAsync(id, ct).ConfigureAwait(false);
        return result.Success;
    }

    /// <inheritdoc/>
    public async Task ClearAllAsync(CancellationToken ct = default)
    {
        // Snapshot the active set before mutating; iterating
        // _store.Active while clearing would skip entries (the
        // collection is built from a live dictionary). Each path's
        // tail is the id (either localId or serverId, ClearAsync
        // resolves which by checking the pending map).
        var ids = _store.Active
            .Select(n => n.Path)
            .Where(p => p.StartsWith(MobPathPrefix, StringComparison.Ordinal))
            .Select(p => p[MobPathPrefix.Length..])
            .ToList();
        foreach (var id in ids)
        {
            await ClearAsync(id, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        // Two-phase: the session-init block is idempotent (load
        // persisted pending raises + spawn retry loops) and runs
        // exactly once per service instance; the reconcile-from-
        // server block is repeatable and runs on every call, since
        // each call should produce a fresh snapshot of the server's
        // active notification list.
        //
        // Splitting matters because the internal connection-edge
        // handler calls only ReconcileFromServerAsync. Without the
        // split, re-running the persisted-pending replay would
        // overwrite each LivePending entry in _pending, orphan the
        // previous CancellationTokenSource (never disposed), and
        // double the retry-loop POST traffic - one orphaned loop +
        // one fresh loop both retrying the same MOB.
        await LoadSessionStateAsync(ct).ConfigureAwait(false);
        await ReconcileFromServerAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Session-init: load any persisted MOB raises from
    /// localStorage and (re-)start the retry-loop + waypoint-create
    /// tasks for each. Idempotent - the <see cref="_sessionLoaded"/>
    /// guard makes the second + N-th call a no-op. Called from
    /// <see cref="InitializeAsync"/>.</summary>
    private async Task LoadSessionStateAsync(CancellationToken ct)
    {
        // Set the gate synchronously BEFORE the first await so a
        // second concurrent caller (e.g. the connect-edge handler
        // firing while the startup call is still hitting localStorage)
        // sees the flag set and falls through without spawning a
        // parallel reload. Blazor WASM is single-threaded so this is
        // atomic from the consumer's perspective.
        if (_sessionLoaded) return;
        _sessionLoaded = true;

        // Recover persisted pending raises. The retry loop picks up
        // from the saved attempt counter so a reload mid-retry
        // doesn't restart the backoff at 1 s.
        var persisted = await LoadPendingAsync(ct).ConfigureAwait(false);
        // Hydrate the resolved-position cache too. Reconcile drops
        // the pending entry on a successful raise; without the cache
        // a subsequent reload would land an emergency banner with no
        // marker, since the in-memory store is session-scoped.
        await _resolvedPositions.LoadAsync(ct).ConfigureAwait(false);
        foreach (var p in persisted)
        {
            // Re-seed the synthetic into the store. The store is
            // session-scoped (in-memory dictionary) so a reload
            // mid-retry needs us to reapply the persisted state.
            _store.Apply(MobPathPrefix + p.LocalId, "emergency", p.Message,
                p.LocalId,
                new NotificationStatus(false, false, false, true, true),
                p.Latitude, p.Longitude);
            var live = new LivePending(p, new CancellationTokenSource());
            _pending[p.LocalId] = live;

            // Resume the waypoint-create loop too if the previous
            // session left WaypointPostPending set (offline raise
            // didn't reach the server before the page closed). The
            // PUT is idempotent on the persisted WaypointId, so a
            // reload-after-network-restored converges to a single
            // server resource without duplicating pins.
            if (_waypoints is not null
                && p.WaypointPostPending
                && p.Latitude is double rLat && p.Longitude is double rLon)
            {
                live.CreateTask = CreateMobWaypointAsync(p.LocalId, rLat, rLon, live.Cts.Token);
            }
            live.Loop = Task.Run(() => RunRaiseLoopAsync(p, live.Cts.Token));
        }
        // Each Apply above fires OnPathChanged; the alarm pipeline
        // subscriber wakes itself N times (cheap; coalesced at the
        // next render tick).
    }

    /// <summary>Repeatable reconcile: pull the server's active
    /// notification list and apply every MOB entry to the local
    /// store. Runs on every <see cref="InitializeAsync"/> call AND
    /// every WS reconnect edge so notifications raised pre-load or
    /// during a disconnect window land on the local banner without
    /// requiring the helm to navigate to a specific page.</summary>
    private async Task ReconcileFromServerAsync(CancellationToken ct)
    {
        // Pull the server's active list so any MOB raised pre-load
        // (this plotter just booted; another plotter's emit) lands
        // on the local store. Silently best-effort: a 4xx / network
        // error here means "nothing to recover", not a hard failure.
        // Each entry is a delta-style envelope { context, path,
        // value }; the notification payload sits under .Value, NOT
        // at the top level. Filter by env.Path so a non-MOB emergency
        // (depth, fire, etc.) doesn't leak into the MOB pipeline -
        // ListActiveAsync returns every active notification regardless
        // of category and this service only owns notifications.mob.*.
        var active = await _api.ListActiveAsync(ct).ConfigureAwait(false);
        if (active is null) return;
        foreach (var (id, env) in active)
        {
            if (env?.Path is null
                || !env.Path.StartsWith(MobPathPrefix, StringComparison.Ordinal))
                continue;
            var dto = env.Value;
            if (dto?.State is null) continue;
            if (string.IsNullOrEmpty(dto.Id) || string.IsNullOrEmpty(id)) continue;
            if (!string.Equals(dto.State, "emergency", StringComparison.Ordinal))
                continue;
            var status = dto.Status is { } s
                ? new NotificationStatus(s.Silenced, s.Acknowledged,
                    s.CanSilence, s.CanAcknowledge, s.CanClear)
                : new NotificationStatus(false, false, false, true, true);
            double? lat = dto.Position?.Latitude;
            double? lon = dto.Position?.Longitude;
            // signalk-server's /mob endpoint discards the POST body's
            // position field so dto.Position is usually null. Fall
            // back to the resolved-position cache so the chart marker
            // recovers across a reload / restart - without it the
            // helm sees an emergency banner with no fix on the chart.
            if (lat is null && lon is null
                && _resolvedPositions.TryGet(dto.Id, out var savedLat, out var savedLon))
            {
                lat = savedLat;
                lon = savedLon;
            }
            // Defensive bounds check: a corrupted server response or a
            // tampered resolved-position cache could carry a lat/lon
            // outside the WGS-84 envelope. Letting that flow through
            // to the chart layer would render off-projection at best,
            // throw deep in the JS interop at worst. Drop the coords
            // (alarm banner still fires; the casualty has no chart fix
            // until the helm raises again with a real GPS reading).
            if (!IsValidLatLon(lat, lon))
            {
                _logger.LogWarning(
                    "[mob] reconcile recovered out-of-range coords for {Id}: lat={Lat} lon={Lon}; dropping",
                    dto.Id, lat, lon);
                lat = null;
                lon = null;
            }
            _store.Apply(env.Path, dto.State, dto.Message, dto.Id, status,
                lat, lon, dto.CreatedAt);
        }
    }

    /// <summary>Reconciliation: any notifications.mob.* path mutated
    /// in the store. If a pending raise has a recorded serverId
    /// matching this path, the WS echo of the server twin has
    /// arrived and the local synthetic at
    /// notifications.mob.&lt;localId&gt; should be torn down so the
    /// helm sees one banner / one marker.
    ///
    /// Fires for local synthetics too (any Apply / Clear triggers
    /// OnPathChanged) but the loop guard on p.ServerId means a
    /// synthetic with no server twin yet is a no-op pass-through.</summary>
    private void ReconcileMobPath(string path)
    {
        if (!path.StartsWith(MobPathPrefix, StringComparison.Ordinal)) return;
        var serverId = path[MobPathPrefix.Length..];
        if (string.IsNullOrEmpty(serverId)) return;
        string? matchedLocalId = null;
        PendingRaise? matchedPending = null;
        foreach (var (localId, live) in _pending)
        {
            if (live.Raise.ServerId is { } sid && string.Equals(sid, serverId, StringComparison.Ordinal))
            {
                matchedLocalId = localId;
                matchedPending = live.Raise;
                break;
            }
        }
        if (matchedLocalId is null) return;
        // Pre-cleanup: signalk-server's /mob endpoint discards the
        // POST body's position field, so the server-twin store entry
        // arrives with Latitude / Longitude == null even when the
        // helm raised the MOB at a known fix. On reload the local
        // synthetic is gone - the server twin is all that remains.
        // Copy the pending raise's recorded position onto the
        // server-twin entry before we drop the synthetic; the
        // position survives reconcile, the alarm pipeline keeps the
        // coords for downstream consumers.
        // Return after the copy: Apply re-fires OnPathChanged, the
        // recursive ReconcileMobPath sees a server-twin that now has
        // coords, falls through to the cleanup branch, and clears
        // the synthetic + pending in one pass. Returning here keeps
        // us off the redundant double-cleanup path.
        // Direct O(1) lookup against the path-keyed store dictionary
        // instead of materialising a fresh snapshot of every active
        // notification per ReconcileMobPath fire. The reconcile runs
        // on every notifications.mob.* mutation - synthetic apply,
        // server WS echo, clear, plus its own re-fire of OnPathChanged
        // inside the Apply below - so the snapshot allocation was per-
        // dispatcher-tick during the MOB-active window.
        ServerNotification? serverEntry = _store.TryGet(path, out var found) ? found : null;
        if (serverEntry is not null
            && serverEntry.Latitude is null
            && serverEntry.Longitude is null
            && matchedPending is { Latitude: { } lat, Longitude: { } lon })
        {
            // Persist the resolved position keyed by serverId so a
            // future reload (after we drop the pending entry below)
            // can recover the coords for the chart marker. The store
            // owns the persist; in-memory state is the source of
            // truth between writes.
            _resolvedPositions.Save(serverId, lat, lon);
            _store.Apply(path, serverEntry.State, serverEntry.Message,
                serverEntry.Id, serverEntry.Status, lat, lon,
                serverEntry.CreatedAt);
            return;
        }
        // Server-twin path was just removed (clear delta) - drop the
        // resolved-position cache entry too so the next raise of a
        // different MOB doesn't pick up a stale fix.
        if (serverEntry is null)
        {
            _resolvedPositions.Forget(serverId);
        }
        // Tear down the local synthetic + the retry loop. The POST
        // already succeeded so the loop is idle; cancelling is
        // defensive against re-entry.
        var localPath = MobPathPrefix + matchedLocalId;
        _store.Clear(localPath);
        CancelAndDropPending(matchedLocalId);
    }

    private async Task RunRaiseLoopAsync(PendingRaise pending, CancellationToken ct)
    {
        // Top-level guard: any unexpected throw out of the body would
        // otherwise fault the Task.Run-launched loop, kill the retry
        // for a life-safety alarm, and surface only as
        // UnobservedTaskException at the AppDomain level. Belt-and-
        // braces - the inner per-attempt catches handle the well-known
        // failure types; this catches the unknowns so the loop is
        // never silently dead.
        try
        {
            await RunRaiseLoopCoreAsync(pending, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal disposal / clear path - loop was asked to stop.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[mob] retry loop crashed for localId={LocalId} attempt={Attempt}; MOB stays as local synthetic until helm clears",
                pending.LocalId, pending.AttemptCount);
        }
    }

    private async Task RunRaiseLoopCoreAsync(PendingRaise pending, CancellationToken ct)
    {
        int attempt = pending.AttemptCount;
        while (!ct.IsCancellationRequested)
        {
            // Honor a deferred clear that landed between iterations.
            // If the helm cleared while the previous POST was failing,
            // there's no point retrying - we have no serverId to
            // DELETE and the local synthetic is already gone.
            if (_pending.TryGetValue(pending.LocalId, out var liveTop)
                && liveTop.Raise.ClearRequested
                && liveTop.Raise.ServerId is null)
            {
                CancelAndDropPending(pending.LocalId);
                return;
            }

            // Pace by the backoff table on retries. First attempt
            // (attempt == 0) skips the wait so the helm sees the
            // POST go out immediately - the visual / chime are
            // already up; we just want the server to know.
            if (attempt > 0)
            {
                int idx = Math.Min(attempt - 1, BackoffSchedule.Length - 1);
                try { await Task.Delay(BackoffSchedule[idx], _time, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }

            ApiResult<string> result;
            try
            {
                result = await _api.RaiseMobAsync(pending.Message, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Per-call timeout SHOULD now surface as ApiResult.Fail
                // (NotificationsApi catches OperationCanceledException
                // when it's a per-call timeout), but a future regression
                // or a transport-level surprise (HttpClient disposed,
                // server abruptly closed the socket) could still throw.
                // Log + retry rather than killing the loop.
                _logger.LogWarning(ex,
                    "[mob] RaiseMobAsync threw on attempt={Attempt}; will retry",
                    attempt);
                attempt++;
                continue;
            }
            attempt++;
            if (result.Success && !string.IsNullOrEmpty(result.Value))
            {
                // POST went through. Record the serverId on the
                // pending entry so the WS echo reconciliation knows
                // which local synthetic to retire.
                if (_pending.TryGetValue(pending.LocalId, out var live))
                {
                    live.Raise = live.Raise with { ServerId = result.Value, AttemptCount = attempt };
                    await PersistPendingAsync(ct).ConfigureAwait(false);

                    // PARA-003: helm cleared while this POST was in
                    // flight. The server has now created a notification
                    // we need to DELETE before its WS echo lands and
                    // re-arms the alarm. Local synthetic + chart
                    // waypoint were already deactivated by ClearAsync
                    // (deferred-clear branch); only the server-side
                    // cleanup is left. Best-effort: a failed DELETE
                    // logs but the local UX has already cleared.
                    if (live.Raise.ClearRequested)
                    {
                        try { _ = _api.ClearAsync(result.Value, CancellationToken.None); }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex,
                                "[mob] deferred-clear DELETE for {ServerId} threw", result.Value);
                        }
                        CancelAndDropPending(pending.LocalId);
                        return;
                    }

                    // Update the paired MOB waypoint's mobAlarmId
                    // from the localId to the server's id so OTHER
                    // plotters seeing notifications.mob.<serverId>
                    // can correlate it with this waypoint via its
                    // mobAlarmId. Best-effort - a failed PUT just
                    // means the correlation key falls back to the
                    // localId (this-plotter-only). Skipped when the
                    // serverId equals the localId (server respected
                    // our id, no update needed) or when the waypoint
                    // wasn't created (offline raise, optional dep).
                    //
                    // Awaits the create task so the order is
                    // deterministic: if the notification POST returns
                    // faster than the waypoint POST (often the case in
                    // production - same SK server, similar latency),
                    // CreatedWaypoint would otherwise be null when we
                    // read it here and the cross-plotter update would
                    // silently skip. Awaiting the task lands us at the
                    // moment CreateMobWaypointAsync has settled (success
                    // or failure); a null return means failure and the
                    // skip is correct.
                    if (live.CreatedWaypoint is { } liveWp
                        && !string.Equals(result.Value, pending.LocalId, StringComparison.Ordinal))
                    {
                        _ = UpdateMobAlarmIdAsync(liveWp, result.Value);
                    }

                    // Order race: the server's WS echo of
                    // notifications.mob.<serverId> can land BEFORE
                    // this HTTP response completes (WS push has fewer
                    // hops than the REST round-trip). In that case
                    // the OnPathChanged event for the WS frame already
                    // fired with pending.ServerId still null, so
                    // ReconcileMobPath couldn't match - and the
                    // local synthetic + server twin would both stay
                    // armed (two banners on the local plotter) until
                    // the next store mutation re-triggered the event.
                    // Probe the store now that ServerId is recorded:
                    // if the server-twin path is already there, tear
                    // down the local synthetic immediately. If it
                    // isn't, the upcoming WS echo will trigger
                    // ReconcileMobPath cleanly.
                    var serverPath = MobPathPrefix + result.Value;
                    // O(1) Contains against the store's path dictionary
                    // instead of allocating an Active snapshot + linear
                    // walk; same lookup result, no per-retry-success
                    // allocation while a live MOB is in flight.
                    if (_store.Contains(serverPath))
                    {
                        _store.Clear(MobPathPrefix + pending.LocalId);
                        CancelAndDropPending(pending.LocalId);
                    }
                }
                return;
            }

            // Failure path: persist the new attempt counter so a
            // reload picks up at the right backoff step.
            if (_pending.TryGetValue(pending.LocalId, out var liveNow))
            {
                liveNow.Raise = liveNow.Raise with { AttemptCount = attempt };
                await PersistPendingAsync(ct).ConfigureAwait(false);
            }
        }
    }

    private void CancelAndDropPending(string localId)
    {
        if (_pending.Remove(localId, out var live))
        {
            try { live.Cts.Cancel(); } catch { /* already cancelled */ }
            live.Cts.Dispose();
        }
        // Persist the trimmed queue so a reload doesn't replay a
        // raise that already succeeded. Fire-and-forget: a transient
        // KV write failure here is recoverable on the next persist
        // (the in-memory map is the source of truth between writes);
        // the worst case is one duplicate raise on reload, which the
        // server-side dedup + ReconcileMobPath then collapses.
        _ = PersistPendingAsync(CancellationToken.None);
    }

    private Task PersistPendingAsync(CancellationToken ct)
    {
        // Empty queue -> remove the storage key so a stale entry
        // doesn't haunt a future reload.
        if (_pending.Count == 0)
        {
            return _kv.RemoveAsync(StorageKey, ct);
        }
        var json = JsonSerializer.Serialize(
            _pending.Values.Select(l => l.Raise).ToArray(),
            OnaPlotter.Services.Json.OnaJsonContext.Default.PendingRaiseArray);
        return _kv.SetAsync(StorageKey, json, ct);
    }

    private async Task<List<PendingRaise>> LoadPendingAsync(CancellationToken ct)
    {
        var json = await _kv.GetAsync(StorageKey, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(json)) return [];
        try
        {
            var arr = JsonSerializer.Deserialize(json,
                OnaPlotter.Services.Json.OnaJsonContext.Default.PendingRaiseArray);
            return arr is null ? [] : [.. arr];
        }
        catch (JsonException)
        {
            // localStorage corruption / schema skew -> treat as
            // empty and overwrite next persist. Better than
            // resurrecting half-parsed garbage.
            return [];
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _store.OnPathChanged -= ReconcileMobPath;
        if (_signalk is not null)
        {
            _signalk.OnConnectionChanged -= HandleConnectionChanged;
        }
        foreach (var live in _pending.Values)
        {
            try { live.Cts.Cancel(); } catch { /* best-effort */ }
            live.Cts.Dispose();
        }
        _pending.Clear();
    }

    /// <summary>Test seam: returns the running retry-loop Task for
    /// the given localId, or null if no loop is tracked. Tests
    /// await this to observe deterministic completion of the
    /// background POST + reconciliation, replacing wall-clock
    /// Task.Delay waits.</summary>
    internal Task? GetRaiseLoopForTest(string localId) =>
        _pending.TryGetValue(localId, out var l) ? l.Loop : null;

    /// <summary>Test-only inspection of the pending-raise record for
    /// a given localId. Returns null if no entry tracked. Used to
    /// pin "the loop captured the assigned serverId" without going
    /// through localStorage.</summary>
    internal PendingRaise? GetPendingForTest(string localId) =>
        _pending.TryGetValue(localId, out var l) ? l.Raise : null;

    /// <summary>WGS-84 envelope check. Both null is fine (no fix yet
    /// is a real state for a MOB raised without GPS); a non-null pair
    /// must be in range. NaN / Infinity are rejected by the comparison
    /// (any comparison against NaN is false).</summary>
    private static bool IsValidLatLon(double? lat, double? lon)
    {
        if (lat is null && lon is null) return true;
        if (lat is not double la || lon is not double lo) return false;
        return la >= -90d && la <= 90d && lo >= -180d && lo <= 180d;
    }

    /// <summary>Bundles the pieces of a live retry loop - the
    /// persistable raise record (replaced as ServerId / AttemptCount
    /// change), the cancel handle, and the running task - into a
    /// single dictionary entry so add/remove can never leave an
    /// orphan CTS or task behind.</summary>
    private sealed class LivePending
    {
        public PendingRaise Raise { get; set; }
        public CancellationTokenSource Cts { get; }
        public Task Loop { get; set; } = Task.CompletedTask;
        /// <summary>Reference to the freshly-created MOB waypoint
        /// instance, stamped right after <see cref="MobService.CreateMobWaypointAsync"/>'s
        /// POST returns. Used by <see cref="MobService.SetMobInactiveAsync"/>
        /// (via the synchronous <see cref="MobService.FindMobWaypoint"/>
        /// fast path in <c>ClearAsync</c>) so the deactivate PUT goes
        /// against a known-good <see cref="SignalkWaypoint"/> without
        /// round-tripping through <c>ResourceStore.GetWaypoint</c>.
        /// The cache lookup was racy: the WS echo of the just-created
        /// waypoint can lag the local POST response, so a same-tick
        /// PUT against the cache would no-op silently and the MOB
        /// waypoint would permanently carry the wrong mobAlarmId. The
        /// in-memory reference avoids the race entirely on the
        /// fast/same-session path; the slow path (cleared MOB after
        /// reload, pending entry retired) still falls back to the
        /// scan in <see cref="MobService.FindMobWaypoint"/>.</summary>
        public OnaPlotter.Models.SignalkWaypoint? CreatedWaypoint { get; set; }
        /// <summary>Awaitable handle to the in-flight
        /// <see cref="MobService.CreateMobWaypointAsync"/> task. The
        /// notification retry loop awaits this BEFORE firing
        /// <see cref="MobService.UpdateMobAlarmIdAsync"/> so the cross-
        /// plotter mobAlarmId update never races the create POST -
        /// previously the loop's success path could read a null
        /// <see cref="CreatedWaypoint"/> and silently skip the update
        /// when the notification POST returned faster than the
        /// resource POST. Null when no waypoint was paired (offline
        /// raise, no lat/lon, no <c>IWaypointApi</c> DI).</summary>
        public Task<OnaPlotter.Models.SignalkWaypoint?>? CreateTask { get; set; }

        public LivePending(PendingRaise raise, CancellationTokenSource cts)
        {
            Raise = raise;
            Cts = cts;
        }
    }
}

/// <summary>Persistable pending-raise record. Survives a page
/// reload via localStorage so an offline emit can resume retry on
/// the next session.
/// <para><see cref="WaypointId"/> is the SignalK resource id of the
/// MOB waypoint <c>MobService.RaiseAsync</c> creates alongside the
/// notification. Stored here so <c>ClearAsync</c> can flip the
/// waypoint's <c>isActive</c> flag without re-resolving the id;
/// also lets the server-id update path PUT the same waypoint
/// once the notification's server-assigned id arrives.</para>
/// <para><see cref="ClearRequested"/> handles the race where the
/// helm clears a MOB while the notification POST is in flight. The
/// retry-loop's success branch checks this flag right after
/// recording the server-assigned id and immediately fires DELETE
/// against that id, preventing the server's WS echo of the just-
/// created notification from re-arming the alarm. Persisted so a
/// reload mid-race resumes the cleanup on the next session.</para></summary>
public sealed record PendingRaise(
    string LocalId,
    string? Message,
    double? Latitude,
    double? Longitude,
    int AttemptCount,
    string? ServerId = null,
    string? WaypointId = null,
    bool ClearRequested = false,
    bool WaypointPostPending = false,
    string? WaypointName = null);
