using OnaPlotter.Models;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Services.Alarms;

/// <summary>
/// Publishes locally-evaluated alarms (CPA / SHALLOW / WIND SHIFT /
/// ANCHOR drag+tide / DEADMAN) to the SignalK v2 notifications API
/// so other plotters connected to the same server see them as
/// <c>notifications.*</c> deltas. Companion to the consume-side
/// pipeline shipped in Phase A: there a server-emitted notification
/// reaches our banner via <see cref="ServerNotificationsAlarmRule"/>;
/// here a local rule's emission reaches every other plotter the same
/// way.
/// <para>
/// Wired by subscribing to <see cref="IAlarmManager.OnAlarmsChanged"/>.
/// On each event we reconcile the live <see cref="IAlarmManager.AllActiveEntries"/>
/// against our own <see cref="_raised"/> map of "we raised this":
/// </para>
/// <list type="bullet">
/// <item>Active locally + not yet raised -> POST raise.</item>
/// <item>Raised at a different severity -> POST again (the server
/// overlays the same path) so other plotters see the escalation.</item>
/// <item>Previously raised + no longer active -> DELETE clear.</item>
/// <item>Active and raised at the same severity -> no-op.</item>
/// </list>
/// <para>
/// At most one request is in flight per alarm key. A change that arrives
/// while a raise or clear is pending is picked up by the reconcile that
/// runs when that request completes. Without this a clear still in flight
/// could land after a newer raise for the same path and wipe it, and two
/// overlapping raises could leave the first server id orphaned.
/// </para>
/// <para>
/// The publisher reads <see cref="IAlarmManager.AllActiveAlarms"/>
/// (uncapped) rather than <see cref="IAlarmManager.ActiveAlarms"/>
/// (capped at <c>MaxActiveAlarms</c> for the banner). The cap is a
/// UI affordance to keep the viewport readable; cross-plotter sync
/// must NOT silently drop the 4th simultaneous alarm just because
/// the local banner can't show it.
/// </para>
/// <para>
/// Fail-soft: every API call is fire-and-forget, with failure
/// removing the path from <see cref="PublishedAlarmTracker"/> so a
/// retry can fire on the next OnAlarmsChanged event. A flaky link
/// to the server doesn't break the local banner - it just delays
/// cross-plotter sync until the link recovers. Failures route
/// through <see cref="ClientErrorRelay"/> so they surface in the
/// SignalK server log for SSH-from-helm debugging at 3 am.
/// </para>
/// <para>
/// Server-emitted alarms (those carrying a non-null acknowledger
/// returned by the bridge rule) are skipped here: re-publishing
/// would create a feedback loop. The bridge rule on every connected
/// plotter (including this one) remains the source of truth for those.
/// </para>
/// </summary>
public sealed class AlarmPublisher : IAsyncDisposable
{
    /// <summary>Wall-clock budget for DisposeAsync cleanup. Each
    /// pending Clear gets its own per-call timeout (see
    /// <see cref="NotificationsApi.CallTimeout"/>); this outer cap
    /// stops a stuck server from blocking page navigation
    /// indefinitely. Two seconds is enough for a healthy LAN to
    /// finish three or four DELETEs in parallel; beyond that the
    /// server's 60 s GC is the safety net.</summary>
    public static readonly TimeSpan DisposeBudget = TimeSpan.FromSeconds(2);

    private readonly IAlarmManager _manager;
    private readonly INotificationsApi _api;
    private readonly PublishedAlarmTracker _tracker;
    private readonly ClientErrorRelay? _relay;
    private readonly ISignalkClient? _signalk;
    private readonly Dictionary<AlarmKey, RaisedEntry> _raised = [];
    private bool _disposed;

    public AlarmPublisher(IAlarmManager manager, INotificationsApi api,
        PublishedAlarmTracker tracker,
        ClientErrorRelay? relay = null,
        ISignalkClient? signalk = null)
    {
        _manager = manager;
        _api = api;
        _tracker = tracker;
        _relay = relay;
        _signalk = signalk;
        _manager.OnAlarmsChanged += HandleAlarmsChanged;
        // Reset our owned-paths view on WebSocket disconnect. After a
        // reconnect, ServerNotificationStore.Reset() has already dropped
        // the consume side; if we held onto _raised, the bridge rule
        // would suppress the re-replay of our own paths and a subsequent
        // local-rule clear could DELETE a stale id while leaving the
        // live one (sticky banner across plotters). Letting the next
        // OnAlarmsChanged tick republish anything still locally active
        // is the cheap correct path.
        if (_signalk is not null)
        {
            _signalk.OnConnectionChanged += HandleConnectionChanged;
        }
    }

    private void HandleAlarmsChanged() => Reconcile(allowRaise: true);

    /// <summary>Diffs active vs raised and starts raise / clear requests
    /// for keys with nothing in flight. <paramref name="allowRaise"/> is
    /// false right after a failed raise so a server that rejects every
    /// POST isn't retried in a tight loop; the next manager change
    /// retries.</summary>
    private void Reconcile(bool allowRaise)
    {
        if (_disposed) return;
        // AllActiveEntries is the UNCAPPED (info, rule) view -
        // ActiveAlarms is the banner stack (capped at MaxActiveAlarms).
        // Cross-plotter sync must not silently drop pile-up alarms,
        // and the rule pairing lets each rule declare its own publish
        // path via IAlarmRule.GetPublishPath.
        var entries = _manager.AllActiveEntries;
        var activeKeys = new HashSet<AlarmKey>(entries.Count);

        // Pass 1: raise newcomers and severity changes. Skip alarms that
        // are already server-emitted (they round-trip through this
        // plotter via the bridge rule - republishing would loop). The
        // acknowledger handle is the marker: only bridge-rule output
        // sets it.
        foreach (var (a, rule) in entries)
        {
            if (a.Acknowledger is not null) continue;
            // Rules that don't participate in cross-plotter publish
            // (e.g. SART - AIS feed already publishes) return null.
            var path = rule.GetPublishPath(a);
            if (string.IsNullOrEmpty(path)) continue;
            var key = new AlarmKey(a.RuleId, a.TargetKey);
            activeKeys.Add(key);
            if (_raised.TryGetValue(key, out var existing))
            {
                if (existing.Busy) continue;
                if (existing.Id is not null && existing.Severity == a.Severity) continue;
            }
            if (!allowRaise) continue;
            var entry = existing ?? new RaisedEntry(path);
            entry.Busy = true;
            _raised[key] = entry;
            // Tag the path as ours before the POST so the bridge rule
            // suppresses the echo even before the id is known.
            _tracker.Add(path);
            _ = TryRaiseAsync(a, key, entry);
        }

        // Pass 2: clear ones that left the active set. Lazy-allocate
        // the snapshot list so the steady state doesn't allocate.
        List<(AlarmKey Key, RaisedEntry Entry)>? toClear = null;
        foreach (var kv in _raised)
        {
            if (activeKeys.Contains(kv.Key) || kv.Value.Busy) continue;
            (toClear ??= new List<(AlarmKey, RaisedEntry)>(2)).Add((kv.Key, kv.Value));
        }
        if (toClear is null) return;
        foreach (var (key, entry) in toClear)
        {
            entry.Busy = true;
            _ = ClearOwnedAsync(key, entry);
        }
    }

    /// <summary>How long server-side notifications survive after the
    /// originating plotter goes silent (signalk-server's notification
    /// GC default). The tracker keeps "we owned this path" entries for
    /// at least this long after disconnect so a server-replay of our
    /// own pre-disconnect notifications doesn't get treated by the
    /// bridge rule as fresh remote alarms after we reconnect.</summary>
    public static readonly TimeSpan DisconnectGracePeriod = TimeSpan.FromSeconds(60);

    /// <summary>Timer that drives the deferred-clear of tracker
    /// entries after the grace period. Held as a field so a second
    /// disconnect within the window resets it cleanly.</summary>
    private System.Threading.Timer? _trackerGraceTimer;

    private void HandleConnectionChanged()
    {
        if (_disposed) return;
        if (_signalk is null) return;
        if (_signalk.IsConnected)
        {
            // Reconnect edge: walk the current local-active set and
            // POST anything that's missing from _raised. A stable
            // alarm (e.g. SHALLOW that's been on for 30 min) does NOT
            // change between disconnect and reconnect, so the
            // OnAlarmsChanged event never fires - without this nudge,
            // _raised stays empty after the disconnect-reset and the
            // alarm is invisible to other plotters until something
            // local mutates. Re-using HandleAlarmsChanged keeps one
            // diff path and the same idempotency story.
            HandleAlarmsChanged();
            return;
        }

        // Disconnect path. Snapshot the paths we currently own + drop
        // the local _raised map (it tracks in-flight raises - their
        // continuations will see _disposed/!IsConnected and bail).
        // Keep the TRACKER paths alive for the server-side GC window
        // (~60s) so the bridge rule continues to filter own-echoes
        // when the server replays our pre-disconnect notifications
        // post-reconnect. Otherwise every brief LTE blip would stack
        // 2-3 banners for the same alarm until the server's GC sweeps
        // it.
        if (_raised.Count == 0) return;
        var ownedPaths = _raised.Values.Select(v => v.Path).ToArray();
        _raised.Clear();
        _ = LogInfoAsync("alarm.publisher reset on WS disconnect (tracker grace " +
            $"{(int)DisconnectGracePeriod.TotalSeconds}s)");

        // Reset any prior grace timer (multiple disconnects in a short
        // window restart the clock from the latest one).
        _trackerGraceTimer?.Dispose();
        _trackerGraceTimer = new System.Threading.Timer(
            _ => SweepTrackerGrace(ownedPaths),
            null, DisconnectGracePeriod, Timeout.InfiniteTimeSpan);
    }

    private void SweepTrackerGrace(string[] paths)
    {
        if (_disposed) return;
        // Drop only the paths still in the tracker AND not re-raised
        // since disconnect (we re-Add via TryRaiseAsync; if the helm's
        // alarm fired again post-reconnect the path's still ours and we
        // keep it). The Remove is a no-op when re-raise already
        // refreshed the entry.
        foreach (var path in paths)
        {
            // If we re-raised this path post-reconnect, leave it alone
            // - the new raise has its own ownership claim.
            if (_raised.Values.Any(v => v.Path == path)) continue;
            _tracker.Remove(path);
        }
    }

    private async Task TryRaiseAsync(AlarmInfo a, AlarmKey key, RaisedEntry entry)
    {
        var path = entry.Path;
        var payload = new NotificationPayload(
            State: a.Severity == AlarmSeverity.Danger ? "alarm" : "warn",
            // Sound + visual on Danger so other plotters' speakers fire
            // too; visual-only on Warn matches the local banner UX.
            Method: a.Severity == AlarmSeverity.Danger
                ? new[] { "visual", "sound" }
                : new[] { "visual" },
            Message: a.Message);

        ApiResult<string> r;
        try
        {
            r = await _api.RaiseAsync(path, payload);
        }
        catch (Exception ex) when (ex is HttpRequestException
                                 || ex is System.Text.Json.JsonException
                                 || ex is OperationCanceledException
                                 || ex is ObjectDisposedException)
        {
            // Socket error, serializer hiccup, per-call timeout, or a
            // disposed HttpClient on shutdown. None should propagate up
            // the fire-and-forget continuation; all deserve a log.
            _ = LogErrorAsync($"alarm.publish raise failed for {path}", ex);
            CompleteFailedRaise(key, entry);
            return;
        }

        if (!r.Success || r.Value is null)
        {
            // Server rejected (404 on a pre-2.21 server, 5xx on a busy
            // one) or 2xx with no parseable id. The local banner still
            // works; logged so a sustained failure is visible.
            _ = LogWarnAsync($"alarm.publish raise non-success for {path}: {r.Error ?? "(no body)"}");
            CompleteFailedRaise(key, entry);
            return;
        }

        string id = r.Value;
        if (!IsCurrent(key, entry))
        {
            // The entry was dropped while we awaited (WS disconnect or
            // dispose). Clear the notification we just created unless a
            // newer entry already owns this path: the server derives the
            // id from (context, path, $source), so that entry's raise
            // overlays the same notification and a clear would wipe it.
            if (!_raised.ContainsKey(key))
            {
                _ = TryClearAsync(id);
                if (!_disposed) _tracker.Remove(path);
            }
            return;
        }

        // A different id for the same path means the server minted a new
        // notification instead of overlaying; retire the previous one.
        if (entry.Id is string previous && previous != id) _ = TryClearAsync(previous);
        entry.Id = id;
        entry.Severity = a.Severity;
        entry.Busy = false;
        _ = LogInfoAsync($"alarm.publish ok rule={a.RuleId} path={path} id={id}");
        // Pick up anything that changed while the raise was in flight
        // (alarm cleared, severity changed again).
        Reconcile(allowRaise: true);
    }

    private void CompleteFailedRaise(AlarmKey key, RaisedEntry entry)
    {
        if (!IsCurrent(key, entry)) return;
        entry.Busy = false;
        if (entry.Id is null)
        {
            // Nothing on the server: release ownership so the next
            // manager change retries.
            _raised.Remove(key);
            _tracker.Remove(entry.Path);
        }
        // Clears only: if the alarm left while the raise was failing,
        // retire whatever this key still owns on the server.
        Reconcile(allowRaise: false);
    }

    private async Task ClearOwnedAsync(AlarmKey key, RaisedEntry entry)
    {
        await TryClearAsync(entry.Id);
        if (!IsCurrent(key, entry)) return;
        // Clear failures are not retried: the server's 60 s GC retires
        // the notification anyway, and holding the key would block a
        // re-raise of the same alarm.
        _raised.Remove(key);
        _tracker.Remove(entry.Path);
        Reconcile(allowRaise: true);
    }

    private bool IsCurrent(AlarmKey key, RaisedEntry entry) =>
        !_disposed && _raised.TryGetValue(key, out var cur) && ReferenceEquals(cur, entry);

    private async Task TryClearAsync(string? id)
    {
        if (id is null) return;
        try
        {
            var r = await _api.ClearAsync(id);
            if (!r.Success)
            {
                _ = LogWarnAsync($"alarm.publish clear non-success for id={id}: {r.Error ?? "(no body)"}");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException
                                 || ex is System.Text.Json.JsonException
                                 || ex is OperationCanceledException
                                 || ex is ObjectDisposedException)
        {
            _ = LogErrorAsync($"alarm.publish clear failed for id={id}", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _manager.OnAlarmsChanged -= HandleAlarmsChanged;
        if (_signalk is not null)
        {
            _signalk.OnConnectionChanged -= HandleConnectionChanged;
        }
        _trackerGraceTimer?.Dispose();
        _trackerGraceTimer = null;
        // Best-effort cleanup so a soft refresh / nav-away doesn't
        // leave stale entries on the server. The 60 s GC would catch
        // them anyway, but ack flows on other plotters work better
        // when stale notifications don't linger. Run the clears in
        // PARALLEL with an outer budget so a stuck server doesn't
        // serialise N awaits and stall page navigation.
        var snapshot = _raised.Values.ToList();
        _raised.Clear();
        foreach (var r in snapshot)
            _tracker.Remove(r.Path);

        if (snapshot.Count == 0) return;
        using var cts = new CancellationTokenSource(DisposeBudget);
        try
        {
            await Task.WhenAll(snapshot.Select(r => TryClearAsync(r.Id)))
                .WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Budget elapsed - the server's GC will catch any clears
            // that didn't finish. We don't surface this through the
            // relay because dispose-time noise just adds to whatever
            // shutdown mess the user is already in.
        }
    }

    private Task LogInfoAsync(string message)
    {
        // Relay is optional for backward-compat with the test ctors
        // that don't wire it. In production the relay is always
        // injected from DI; logs land in the SK server log via the
        // /log endpoint and are reviewable via SSH at the helm.
        if (_relay is null) return Task.CompletedTask;
        return _relay.ReportAsync($"[INFO] {message}");
    }

    private Task LogWarnAsync(string message)
    {
        if (_relay is null) return Task.CompletedTask;
        return _relay.ReportAsync($"[WARN] {message}");
    }

    private Task LogErrorAsync(string message, Exception ex)
    {
        if (_relay is null) return Task.CompletedTask;
        return _relay.ReportAsync(message, ex);
    }

    private readonly record struct AlarmKey(AlarmRuleId RuleId, string? TargetKey);

    /// <summary>Per-key publish state. <c>Id</c> / <c>Severity</c> describe
    /// what the server last confirmed (Id null until the first raise
    /// succeeds); <c>Busy</c> is true while a raise or clear for this key
    /// is in flight. Mutable and compared by reference so a completion
    /// can tell whether its entry was replaced (disconnect, dispose)
    /// while it awaited.</summary>
    private sealed class RaisedEntry(string path)
    {
        public string Path { get; } = path;
        public string? Id { get; set; }
        public AlarmSeverity Severity { get; set; }
        public bool Busy { get; set; }
    }
}
