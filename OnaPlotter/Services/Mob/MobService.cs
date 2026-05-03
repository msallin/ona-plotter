using System.Text.Json;
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
/// successful raise -- acceptable.</para>
///
/// <para>Persistence: the pending-raise queue is written to
/// localStorage (key <c>mob.pendingRaise.v1</c>) so an offline
/// emit survives a reload. On <see cref="InitializeAsync"/>, the
/// queue is replayed and the server's active list is pulled to
/// recover MOBs raised before this plotter was around.</para>
/// </summary>
public sealed class MobService : IMobService, IDisposable
{
    private const string StorageKey = "mob.pendingRaise.v1";
    /// <summary>localStorage key for the resolved-position cache --
    /// a serverId -> (lat, lon) map written when reconcile copies
    /// the position from a successful local synthetic onto the
    /// server twin. Lets a future reload / restart enrich the
    /// position-less server twin (signalk-server discards
    /// <c>position</c> from the /mob POST body so the canonical
    /// store of the lat/lon is here, not on the server).</summary>
    private const string ResolvedPositionsKey = "mob.resolvedPositions.v1";
    private const string MobPathPrefix = "notifications.mob.";
    private const string MobValueId = "mob";

    /// <summary>Default banner copy when the helm doesn't pass an
    /// explicit message. Single source of truth -- Map.razor passes
    /// null and inherits this so a future i18n / wording change has
    /// one site to edit.</summary>
    public const string DefaultRaiseMessage = "Person Overboard!";

    /// <summary>Backoff schedule for the REST retry loop. After
    /// the table is exhausted the service falls back to the last
    /// entry indefinitely -- per spec, "never hide or block the
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
    private readonly TimeProvider _time;

    /// <summary>Keyed by localId. Survives reload via localStorage;
    /// the in-memory map is the source of truth between persists.</summary>
    private readonly Dictionary<string, PendingRaise> _pending = new(StringComparer.Ordinal);

    /// <summary>Per-pending CTS so a successful echo or explicit
    /// cancel can stop the retry loop cleanly.</summary>
    private readonly Dictionary<string, CancellationTokenSource> _pendingCts =
        new(StringComparer.Ordinal);

    /// <summary>Per-pending retry-loop Task. Test seam: a test can
    /// <c>await GetRaiseLoopAsync(localId)</c> instead of polling
    /// the fake API with wall-clock waits, which removes the flake
    /// vector identified in the review (TEST-004 / TEST-005).
    /// Production callers don't need this; it stays internal.</summary>
    private readonly Dictionary<string, Task> _pendingTasks =
        new(StringComparer.Ordinal);

    /// <summary>Cache of MOB positions keyed by serverId, persisted to
    /// localStorage. Populated when reconcile transfers the local
    /// synthetic's position onto the server twin and consulted on
    /// <see cref="InitializeAsync"/> when ListActiveAsync returns a
    /// server-twin entry without position. Without this cache the
    /// chart marker would vanish across a session boundary: the
    /// previous-session reconcile drops the pending entry once the
    /// raise succeeds, so the position lives only on the (in-memory)
    /// server-twin store entry. New session = new store, gone.</summary>
    private readonly Dictionary<string, (double Lat, double Lon)> _resolvedPositions =
        new(StringComparer.Ordinal);

    private bool _disposed;

    public MobService(
        INotificationsApi api,
        ServerNotificationStore store,
        IKeyValueStore kv,
        TimeProvider time)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _kv = kv ?? throw new ArgumentNullException(nameof(kv));
        _time = time ?? TimeProvider.System;

        // Reconciliation: when any notifications.mob.* path mutates in
        // the store -- WS echo from the server, our own synthetic, or
        // a clear -- ReconcileMobPath checks if a pending raise's
        // recorded serverId matches the path and, if so, tears down
        // the local synthetic so the helm sees one banner / one marker.
        // Also serves the alarm-pipeline wake-up: MainLayout
        // subscribes to the same OnPathChanged and re-evaluates, so
        // the synthetic path triggers banner + audio without a
        // dedicated callback.
        _store.OnPathChanged += ReconcileMobPath;
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
        var pending = new PendingRaise(
            LocalId: localId,
            Message: msg,
            Latitude: latitude,
            Longitude: longitude,
            AttemptCount: 0);
        _pending[localId] = pending;
        await PersistPendingAsync(ct);

        // Background retry. The loop owns its own CTS so cancellations
        // (echo arrival, dispose) tear it down cleanly. The Task is
        // tracked so tests can await it -- production callers ignore
        // the handle.
        var cts = new CancellationTokenSource();
        _pendingCts[localId] = cts;
        var loopTask = Task.Run(() => RunRaiseLoopAsync(pending, cts.Token));
        _pendingTasks[localId] = loopTask;

        return localId;
    }

    /// <inheritdoc/>
    public async Task<bool> ClearAsync(string id, CancellationToken ct = default)
    {
        // Resolve "is this a pending local id (no server twin yet)
        // or an already-synced server id?" If a pending raise's
        // localId matches OR its serverId matches, cancel the retry
        // loop first -- otherwise the loop completes a successful
        // POST after the helm has cleared, and the WS echo of the
        // server twin resurrects the MOB. SKEP-001 in the review.
        string? matchedLocalId = null;
        bool hasServerSide = false;
        foreach (var (localId, p) in _pending)
        {
            if (string.Equals(localId, id, StringComparison.Ordinal))
            {
                matchedLocalId = localId;
                hasServerSide = p.ServerId is not null;
                break;
            }
            if (p.ServerId is { } sid && string.Equals(sid, id, StringComparison.Ordinal))
            {
                matchedLocalId = localId;
                hasServerSide = true;
                break;
            }
        }
        if (matchedLocalId is not null)
        {
            CancelAndDropPending(matchedLocalId);
        }

        // Drop the local synthetic at notifications.mob.<localId>
        // (if we matched one) AND the path the helm passed (could
        // be the server twin path notifications.mob.<serverId>).
        // Safe-no-ops on absent paths.
        if (matchedLocalId is not null)
        {
            _store.Clear(MobPathPrefix + matchedLocalId);
        }
        _store.Clear(MobPathPrefix + id);

        // Drop the resolved-position cache entry for this MOB so a
        // future raise of an unrelated MOB doesn't pick up a stale
        // fix from a long-resolved casualty.
        if (_resolvedPositions.Remove(id))
        {
            _ = PersistResolvedPositionsAsync(CancellationToken.None);
        }

        // Skip the REST call when we know there's no server-side
        // entry to clear. Calling DELETE /<localId> would 404 (the
        // server never saw that id) and the call wastes a round-
        // trip + can confuse a future observer in the server log.
        if (matchedLocalId is not null && !hasServerSide)
        {
            return true;
        }

        // DELETE /signalk/v2/api/notifications/<id> -- the actual
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
        // Recover persisted pending raises. The retry loop picks up
        // from the saved attempt counter so a reload mid-retry
        // doesn't restart the backoff at 1 s.
        var persisted = await LoadPendingAsync(ct).ConfigureAwait(false);
        // Recover the resolved-position cache too so the chart marker
        // can survive a reload that happens AFTER reconcile already
        // dropped the pending entry on the previous session.
        await LoadResolvedPositionsAsync(ct).ConfigureAwait(false);
        foreach (var p in persisted)
        {
            // Re-seed the synthetic into the store -- the previous
            // session's store entries are gone now (in-memory state
            // doesn't survive reload).
            _store.Apply(MobPathPrefix + p.LocalId, "emergency", p.Message,
                p.LocalId,
                new NotificationStatus(false, false, false, true, true),
                p.Latitude, p.Longitude);
            _pending[p.LocalId] = p;
            var cts = new CancellationTokenSource();
            _pendingCts[p.LocalId] = cts;
            _pendingTasks[p.LocalId] = Task.Run(() => RunRaiseLoopAsync(p, cts.Token));
        }
        // Each Apply above fires OnPathChanged; the alarm pipeline
        // subscriber wakes itself N times (cheap; coalesced at the
        // next render tick).

        // Pull the server's active list so any MOB raised pre-load
        // (this plotter just booted; another plotter's emit) lands
        // on the local store. Silently best-effort: a 4xx / network
        // error here means "nothing to recover", not a hard failure.
        var active = await _api.ListActiveAsync(ct).ConfigureAwait(false);
        if (active is null) return;
        foreach (var (id, dto) in active)
        {
            if (dto.State is null) continue;
            // List returns every active notification; this service
            // owns the MOB pipeline so we filter by state=emergency
            // (a coarse but acceptable proxy -- non-MOB emergencies
            // are rare on SK servers and the chart renderer drops
            // the marker on the next state transition if we mis-
            // classify). dto.Id is the canonical id per spec.
            if (string.IsNullOrEmpty(dto.Id) || string.IsNullOrEmpty(id)) continue;
            if (!string.Equals(dto.State, "emergency", StringComparison.Ordinal))
                continue;
            var path = MobPathPrefix + dto.Id;
            var status = dto.Status is { } s
                ? new NotificationStatus(s.Silenced, s.Acknowledged,
                    s.CanSilence, s.CanAcknowledge, s.CanClear)
                : new NotificationStatus(false, false, false, true, true);
            double? lat = dto.Position?.Latitude;
            double? lon = dto.Position?.Longitude;
            // Server discards position from /mob POST body so dto.Position
            // is usually null. Fall back to the resolved-position cache
            // (written by reconcile in a previous session) so the chart
            // marker recovers across a reload / restart.
            if (lat is null && lon is null
                && _resolvedPositions.TryGetValue(dto.Id, out var saved))
            {
                lat = saved.Lat;
                lon = saved.Lon;
            }
            _store.Apply(path, dto.State, dto.Message, dto.Id, status,
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
        foreach (var (localId, p) in _pending)
        {
            if (p.ServerId is { } sid && string.Equals(sid, serverId, StringComparison.Ordinal))
            {
                matchedLocalId = localId;
                matchedPending = p;
                break;
            }
        }
        if (matchedLocalId is null) return;
        // Pre-cleanup: signalk-server's /mob endpoint discards the
        // POST body's position field, so the server-twin store entry
        // arrives with Latitude / Longitude == null even when the
        // helm raised the MOB at a known fix. The MOB chart marker
        // can't draw without coords (MobChartRenderer bails on null
        // lat/lon), and on reload the local synthetic is gone -- the
        // server twin is all that remains. Copy the pending raise's
        // recorded position onto the server-twin entry before we
        // drop the synthetic; the position survives reconcile, the
        // marker survives reload.
        // Return after the copy: Apply re-fires OnPathChanged, the
        // recursive ReconcileMobPath sees a server-twin that now has
        // coords, falls through to the cleanup branch, and clears
        // the synthetic + pending in one pass. Returning here keeps
        // us off the redundant double-cleanup path.
        var serverEntry = _store.Active.FirstOrDefault(n =>
            string.Equals(n.Path, path, StringComparison.Ordinal));
        if (serverEntry is not null
            && serverEntry.Latitude is null
            && serverEntry.Longitude is null
            && matchedPending is { Latitude: { } lat, Longitude: { } lon })
        {
            // Persist the resolved position keyed by serverId so a
            // future reload (after we drop the pending entry below)
            // can recover the coords for the chart marker. Fire-
            // and-forget; the in-memory dict is the source of truth
            // between writes.
            _resolvedPositions[serverId] = (lat, lon);
            _ = PersistResolvedPositionsAsync(CancellationToken.None);
            _store.Apply(path, serverEntry.State, serverEntry.Message,
                serverEntry.Id, serverEntry.Status, lat, lon,
                serverEntry.CreatedAt);
            return;
        }
        // Server-twin path was just removed (clear delta) -- drop the
        // resolved-position cache entry too so the next raise of a
        // different MOB doesn't pick up a stale fix.
        if (serverEntry is null)
        {
            if (_resolvedPositions.Remove(serverId))
            {
                _ = PersistResolvedPositionsAsync(CancellationToken.None);
            }
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
        int attempt = pending.AttemptCount;
        while (!ct.IsCancellationRequested)
        {
            // Pace by the backoff table on retries. First attempt
            // (attempt == 0) skips the wait so the helm sees the
            // POST go out immediately -- the visual / chime are
            // already up; we just want the server to know.
            if (attempt > 0)
            {
                int idx = Math.Min(attempt - 1, BackoffSchedule.Length - 1);
                try { await Task.Delay(BackoffSchedule[idx], _time, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }

            var result = await _api.RaiseMobAsync(pending.Message, ct).ConfigureAwait(false);
            attempt++;
            if (result.Success && !string.IsNullOrEmpty(result.Value))
            {
                // POST went through. Record the serverId on the
                // pending entry so the WS echo reconciliation knows
                // which local synthetic to retire.
                if (_pending.TryGetValue(pending.LocalId, out var live))
                {
                    var updated = live with { ServerId = result.Value, AttemptCount = attempt };
                    _pending[pending.LocalId] = updated;
                    await PersistPendingAsync(ct).ConfigureAwait(false);

                    // Race fix: the server's WS echo can arrive at
                    // notifications.mob.<serverId> BEFORE this HTTP
                    // response completes (WS push has fewer hops
                    // than the REST round-trip). When that happens
                    // the OnPathChanged event for the WS frame
                    // already fired, but ReconcileMobPath couldn't
                    // match because pending.ServerId was still null.
                    // Result: TWO banners on the local plotter
                    // (local synthetic at <localId> + server twin
                    // at <serverId>) until the next store mutation
                    // happens to fire OnPathChanged again. Probe
                    // the store now: if the server-twin path is
                    // already there, tear down the local synthetic
                    // immediately. If it isn't, the upcoming WS
                    // echo will trigger ReconcileMobPath cleanly.
                    var serverPath = MobPathPrefix + result.Value;
                    if (_store.Active.Any(n => n.Path == serverPath))
                    {
                        _store.Clear(MobPathPrefix + pending.LocalId);
                        CancelAndDropPending(pending.LocalId);
                    }
                }
                return;
            }

            // Failure path: persist the new attempt counter so a
            // reload picks up at the right backoff step.
            if (_pending.TryGetValue(pending.LocalId, out var pendNow))
            {
                _pending[pending.LocalId] = pendNow with { AttemptCount = attempt };
                await PersistPendingAsync(ct).ConfigureAwait(false);
            }
        }
    }

    private void CancelAndDropPending(string localId)
    {
        if (_pendingCts.TryGetValue(localId, out var cts))
        {
            try { cts.Cancel(); } catch { /* already cancelled */ }
            cts.Dispose();
            _pendingCts.Remove(localId);
        }
        _pending.Remove(localId);
        _pendingTasks.Remove(localId);
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
        var json = JsonSerializer.Serialize(_pending.Values.ToArray(),
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

    private Task PersistResolvedPositionsAsync(CancellationToken ct)
    {
        if (_resolvedPositions.Count == 0)
        {
            return _kv.RemoveAsync(ResolvedPositionsKey, ct);
        }
        var arr = _resolvedPositions
            .Select(kv => new ResolvedMobPosition(kv.Key, kv.Value.Lat, kv.Value.Lon))
            .ToArray();
        var json = JsonSerializer.Serialize(arr,
            OnaPlotter.Services.Json.OnaJsonContext.Default.ResolvedMobPositionArray);
        return _kv.SetAsync(ResolvedPositionsKey, json, ct);
    }

    private async Task LoadResolvedPositionsAsync(CancellationToken ct)
    {
        var json = await _kv.GetAsync(ResolvedPositionsKey, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(json)) return;
        try
        {
            var arr = JsonSerializer.Deserialize(json,
                OnaPlotter.Services.Json.OnaJsonContext.Default.ResolvedMobPositionArray);
            if (arr is null) return;
            foreach (var p in arr)
            {
                if (string.IsNullOrEmpty(p.ServerId)) continue;
                _resolvedPositions[p.ServerId] = (p.Latitude, p.Longitude);
            }
        }
        catch (JsonException)
        {
            // localStorage corruption / schema skew -> drop the
            // cache and start clean. The chart marker can't appear
            // until the next raise, but the alarm pipeline still
            // works -- safe degraded mode.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _store.OnPathChanged -= ReconcileMobPath;
        foreach (var cts in _pendingCts.Values)
        {
            try { cts.Cancel(); } catch { /* best-effort */ }
            cts.Dispose();
        }
        _pendingCts.Clear();
        _pendingTasks.Clear();
    }

    /// <summary>Test seam: returns the running retry-loop Task for
    /// the given localId, or null if no loop is tracked. Tests
    /// await this to observe deterministic completion of the
    /// background POST + reconciliation, replacing wall-clock
    /// Task.Delay waits.</summary>
    internal Task? GetRaiseLoopForTest(string localId) =>
        _pendingTasks.TryGetValue(localId, out var t) ? t : null;
}

/// <summary>Persistable pending-raise record. Survives a page
/// reload via localStorage so an offline emit can resume retry on
/// the next session.</summary>
public sealed record PendingRaise(
    string LocalId,
    string? Message,
    double? Latitude,
    double? Longitude,
    int AttemptCount,
    string? ServerId = null);

/// <summary>Persistable map entry for the resolved-position cache.
/// signalk-server's /mob endpoint discards the POST body's position
/// field, so this client-side cache is the canonical store of the
/// MOB lat/lon. Written on reconcile (when transferring position
/// from local synthetic to server twin) and consumed on
/// <see cref="MobService.InitializeAsync"/> to recover the position
/// across a session boundary.</summary>
public sealed record ResolvedMobPosition(
    string ServerId,
    double Latitude,
    double Longitude);
