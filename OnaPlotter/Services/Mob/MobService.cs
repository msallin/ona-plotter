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
/// <para>Persistence: the pending-raise queue is written to
/// localStorage (key <c>mob.pendingRaise.v1</c>) so an offline
/// emit survives a reload. On <see cref="InitializeAsync"/>, the
/// queue is replayed and the server's active list is pulled to
/// recover MOBs raised before this plotter was around.</para>
/// </summary>
public sealed class MobService : IMobService, IDisposable
{
    private const string StorageKey = "mob.pendingRaise.v1";
    private const string MobPathPrefix = "notifications.mob.";
    private const string MobValueId = "mob";

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
    private readonly Action _notifyChanged;

    /// <summary>Keyed by localId. Survives reload via localStorage;
    /// the in-memory map is the source of truth between persists.</summary>
    private readonly Dictionary<string, PendingRaise> _pending = new(StringComparer.Ordinal);

    /// <summary>Per-pending CTS so a successful echo or explicit
    /// cancel can stop the retry loop cleanly.</summary>
    private readonly Dictionary<string, CancellationTokenSource> _pendingCts =
        new(StringComparer.Ordinal);

    private bool _disposed;

    public MobService(
        INotificationsApi api,
        ServerNotificationStore store,
        IKeyValueStore kv,
        TimeProvider time,
        Action onChanged)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _kv = kv ?? throw new ArgumentNullException(nameof(kv));
        _time = time ?? TimeProvider.System;
        _notifyChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));

        // Reconciliation: when the WS echo lands at
        // notifications.mob.<serverId>, the store fires OnPathChanged.
        // We match the new path against any pending raise that has a
        // serverId set (from a successful POST) and tear down its
        // local synthetic so the helm sees one banner / one marker.
        _store.OnPathChanged += HandleStorePathChanged;
    }

    private void HandleStorePathChanged(string path)
    {
        if (!path.StartsWith(MobPathPrefix, StringComparison.Ordinal)) return;
        var idFromPath = path[MobPathPrefix.Length..];
        if (string.IsNullOrEmpty(idFromPath)) return;
        OnServerEcho(path, idFromPath);
    }

    /// <inheritdoc/>
    public async Task<string> RaiseAsync(
        string? message, double? latitude, double? longitude,
        CancellationToken ct = default)
    {
        var localId = Guid.NewGuid().ToString();
        var msg = string.IsNullOrWhiteSpace(message) ? "Person Overboard!" : message;
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
        if (stored) _notifyChanged();

        // Track + persist the pending raise so a reload mid-retry
        // resumes where it left off.
        var pending = new PendingRaise(
            LocalId: localId,
            Message: msg,
            Latitude: latitude,
            Longitude: longitude,
            CreatedAtUtc: _time.GetUtcNow().UtcDateTime,
            AttemptCount: 0);
        _pending[localId] = pending;
        await PersistPendingAsync(ct);

        // Background retry. Fire-and-forget; the loop owns its own
        // CTS so cancellations (echo arrival, dispose) tear it down
        // cleanly.
        var cts = new CancellationTokenSource();
        _pendingCts[localId] = cts;
        _ = Task.Run(() => RunRaiseLoopAsync(pending, cts.Token));

        return localId;
    }

    /// <inheritdoc/>
    public async Task<bool> AcknowledgeAsync(string serverId, CancellationToken ct = default)
    {
        var path = MobPathPrefix + serverId;
        // Optimistic local update: drop the active entry (the alarm
        // pipeline auto-clears the banner on the next tick). The WS
        // echo's status.acknowledged=true would do the same, so the
        // round-trip lands as a no-op.
        bool wasActive = _store.Clear(path);
        if (wasActive) _notifyChanged();

        var result = await _api.AcknowledgeAsync(serverId, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            // Rollback: the server still has the alarm armed, so
            // the next WS delta tick will re-arm us. Nothing to
            // restore by hand -- the store is eventually consistent.
            return false;
        }
        return true;
    }

    /// <inheritdoc/>
    public async Task<bool> ClearAsync(string serverId, CancellationToken ct = default)
    {
        var path = MobPathPrefix + serverId;
        bool wasActive = _store.Clear(path);
        if (wasActive) _notifyChanged();

        var result = await _api.ClearByActionAsync(serverId, ct).ConfigureAwait(false);
        return result.Success;
    }

    /// <inheritdoc/>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        // Recover persisted pending raises. The retry loop picks up
        // from the saved attempt counter so a reload mid-retry
        // doesn't restart the backoff at 1 s.
        var persisted = await LoadPendingAsync(ct).ConfigureAwait(false);
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
            _ = Task.Run(() => RunRaiseLoopAsync(p, cts.Token));
        }
        if (persisted.Count > 0) _notifyChanged();

        // Pull the server's active list so any MOB raised pre-load
        // (this plotter just booted; another plotter's emit) lands
        // on the local store. Silently best-effort: a 4xx / network
        // error here means "nothing to recover", not a hard failure.
        var active = await _api.ListActiveAsync(ct).ConfigureAwait(false);
        if (active is null) return;
        bool changed = false;
        foreach (var (id, dto) in active)
        {
            if (dto.State is null) continue;
            // Filter to MOB notifications only -- the list endpoint
            // returns ALL active notifications, but this service
            // owns only the MOB pipeline. Match against the value-
            // level id the spec uses as the canonical key.
            if (string.IsNullOrEmpty(dto.Id) || string.IsNullOrEmpty(id)) continue;
            // SK servers we've seen put the MOB id in `id` and key
            // by the same id; trust dto.Id since the spec calls it
            // canonical.
            // Heuristic: a notification carrying state=emergency and
            // method containing "sound" + "visual" is a safety
            // alarm. The list endpoint returns the path inside the
            // value's status -- but to keep this contained we
            // treat any state=emergency entry as a MOB candidate.
            // The chart-marker layer drops the marker if the state
            // later transitions to normal; no harm done.
            if (!string.Equals(dto.State, "emergency", StringComparison.Ordinal))
                continue;
            var path = MobPathPrefix + dto.Id;
            var status = dto.Status is { } s
                ? new NotificationStatus(s.Silenced, s.Acknowledged,
                    s.CanSilence, s.CanAcknowledge, s.CanClear)
                : new NotificationStatus(false, false, false, true, true);
            double? lat = dto.Position?.Latitude;
            double? lon = dto.Position?.Longitude;
            if (_store.Apply(path, dto.State, dto.Message, dto.Id, status, lat, lon))
                changed = true;
        }
        if (changed) _notifyChanged();
    }

    /// <inheritdoc/>
    public void OnServerEcho(string path, string? serverId)
    {
        if (!path.StartsWith(MobPathPrefix, StringComparison.Ordinal)) return;
        if (string.IsNullOrEmpty(serverId)) return;
        // Look for a pending raise whose REST POST already returned
        // this serverId. When we find one, the local synthetic at
        // notifications.mob.<localId> can go away -- the server's
        // canonical entry at notifications.mob.<serverId> is now in
        // the store. Without this drop, both entries stay alive and
        // the helm sees two banners + two markers.
        string? matchedLocalId = null;
        foreach (var (localId, p) in _pending)
        {
            if (p.ServerId is { } sid && string.Equals(sid, serverId, StringComparison.Ordinal))
            {
                matchedLocalId = localId;
                break;
            }
        }
        if (matchedLocalId is null) return;
        // Tear down the local synthetic + the retry loop (the POST
        // already succeeded; the loop is idle by now but cancelling
        // is defensive against re-entry).
        var localPath = MobPathPrefix + matchedLocalId;
        if (_store.Clear(localPath)) _notifyChanged();
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
                // which local synthetic to retire. We do NOT remove
                // the synthetic here -- the WS echo arrives via
                // OnServerEcho and removes it then. If the echo
                // never arrives (server dropped it for whatever
                // reason) the helm still sees the synthetic;
                // safety > correctness.
                if (_pending.TryGetValue(pending.LocalId, out var live))
                {
                    var updated = live with { ServerId = result.Value, AttemptCount = attempt };
                    _pending[pending.LocalId] = updated;
                    await PersistPendingAsync(ct).ConfigureAwait(false);
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
        // Persist the trimmed queue so a reload doesn't replay a
        // raise that already succeeded.
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _store.OnPathChanged -= HandleStorePathChanged;
        foreach (var cts in _pendingCts.Values)
        {
            try { cts.Cancel(); } catch { /* best-effort */ }
            cts.Dispose();
        }
        _pendingCts.Clear();
    }
}

/// <summary>Persistable pending-raise record. Survives a page
/// reload via localStorage so an offline emit can resume retry on
/// the next session.</summary>
public sealed record PendingRaise(
    string LocalId,
    string? Message,
    double? Latitude,
    double? Longitude,
    DateTime CreatedAtUtc,
    int AttemptCount,
    string? ServerId = null);
