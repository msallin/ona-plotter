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
/// On each event we diff the live <see cref="IAlarmManager.AllActiveAlarms"/>
/// against our own <see cref="_raised"/> map of "we raised this id":
/// </para>
/// <list type="bullet">
/// <item>Active locally + not yet raised -> POST raise.</item>
/// <item>Previously raised + no longer active -> DELETE clear.</item>
/// <item>Active and already raised -> no-op (idempotent on the
/// server side: a re-raise overlays).</item>
/// </list>
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
    private readonly SignalkClient? _signalk;
    private readonly Dictionary<AlarmKey, RaisedEntry> _raised = [];
    private bool _disposed;

    public AlarmPublisher(IAlarmManager manager, INotificationsApi api,
        PublishedAlarmTracker tracker,
        ClientErrorRelay? relay = null,
        SignalkClient? signalk = null)
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

    /// <summary>Async-aware reaction to the alarm-manager's change
    /// event. Diffs active vs raised, fires raise/clear as needed.
    /// Each API call is fire-and-forget; the next OnAlarmsChanged
    /// reconciles state if a call fails.</summary>
    private void HandleAlarmsChanged()
    {
        if (_disposed) return;
        // AllActiveEntries is the UNCAPPED (info, rule) view -
        // ActiveAlarms is the banner stack (capped at MaxActiveAlarms).
        // Cross-plotter sync must not silently drop pile-up alarms,
        // and the rule pairing lets each rule declare its own publish
        // path via IAlarmRule.GetPublishPath rather than the publisher
        // reverse-engineering one from the title.
        var entries = _manager.AllActiveEntries;
        var activeKeys = new HashSet<AlarmKey>(entries.Count);

        // Pass 1: raise newcomers. Skip alarms that are already
        // server-emitted (they round-trip through this plotter via the
        // bridge rule - republishing would loop). The acknowledger
        // handle is the marker: only bridge-rule output sets it.
        foreach (var (a, rule) in entries)
        {
            if (a.Acknowledger is not null) continue;
            // Ask the rule itself for the path. Rules that don't
            // participate in cross-plotter publish (e.g. SART - AIS
            // feed already publishes; bridge rule - already came
            // from the server) return null and we skip.
            var path = rule.GetPublishPath(a);
            if (string.IsNullOrEmpty(path)) continue;
            var key = new AlarmKey(a.Title, a.TargetKey);
            activeKeys.Add(key);
            if (_raised.ContainsKey(key)) continue;
            _ = TryRaiseAsync(a, path, key);
        }

        // Pass 2: clear ones that left the active set. Snapshot the
        // keys to avoid mutating the dictionary mid-iteration.
        // Lazy-allocate the snapshot list so the steady state - "no
        // alarms cleared this tick", which is most ticks once raise
        // settles - doesn't allocate an enumerator chain + List<>.
        List<(AlarmKey Key, RaisedEntry Raised)>? toClear = null;
        foreach (var kv in _raised)
        {
            if (activeKeys.Contains(kv.Key)) continue;
            (toClear ??= new List<(AlarmKey, RaisedEntry)>(2))
                .Add((kv.Key, kv.Value));
        }
        if (toClear is null) return;
        foreach (var (key, raised) in toClear)
        {
            _raised.Remove(key);
            _tracker.Remove(raised.Path);
            _ = TryClearAsync(raised.Id);
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

    private async Task TryRaiseAsync(AlarmInfo a, string path, AlarmKey key)
    {
        // Reserve the slot synchronously so a second event before the
        // POST resolves doesn't double-fire. Id stays null while the
        // raise is in flight; HandleAlarmsChanged uses presence-in-dict
        // to decide "raised vs not", and the late-clear path below
        // checks the id for null to know the response hasn't arrived
        // yet. We also tag the path as ours immediately so the bridge
        // rule suppresses the echo even before the id is finalised.
        _raised[key] = new RaisedEntry(path, Id: null);
        _tracker.Add(path);

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
        catch (HttpRequestException ex)
        {
            // ResourceHttp normally swallows HttpRequestException, but
            // the per-call timeout CTS surfaces TaskCanceledException
            // (a subclass of HttpRequestException-adjacent path) and
            // some socket-level errors propagate directly. Drop the
            // optimistic state so the next change retries; surface
            // through the relay so a stuck publish flow is visible.
            _raised.Remove(key);
            _tracker.Remove(path);
            _ = LogErrorAsync($"alarm.publish raise failed for {path}", ex);
            return;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException
                                 || ex is TaskCanceledException
                                 || ex is OperationCanceledException
                                 || ex is ObjectDisposedException)
        {
            // Narrow catch set: serializer hiccup, per-call timeout, or
            // disposed HttpClient on shutdown. None of these should
            // propagate up the fire-and-forget continuation; all of
            // them deserve a log so the failure is fixable post-hoc.
            _raised.Remove(key);
            _tracker.Remove(path);
            _ = LogErrorAsync($"alarm.publish raise failed for {path}", ex);
            return;
        }

        if (!r.Success || r.Value is null)
        {
            // Server rejected (404 on a pre-2.21 server, 5xx on a busy
            // one) or 2xx with no parseable id. Release the slot so
            // the next OnAlarmsChanged can try again; meanwhile the
            // local banner still works. Logged so a sustained failure
            // is visible.
            _raised.Remove(key);
            _tracker.Remove(path);
            _ = LogWarnAsync($"alarm.publish raise non-success for {path}: {r.Error ?? "(no body)"}");
            return;
        }

        // Late-clear race: the alarm may have left the active set while
        // we were awaiting. If our entry was removed by HandleAlarmsChanged
        // in the meantime, fire the clear now - otherwise the server
        // notification would linger until the 60s GC.
        if (!_raised.ContainsKey(key))
        {
            _ = TryClearAsync(r.Value);
            _tracker.Remove(path);
            return;
        }

        _raised[key] = new RaisedEntry(path, r.Value);
        _ = LogInfoAsync($"alarm.publish ok title={a.Title} path={path} id={r.Value}");
    }

    private async Task TryClearAsync(string? id)
    {
        if (id is null)
        {
            // Race: a clear arrived before the raise resolved. The
            // raise-completion path will issue the clear when it sees
            // the slot was already removed. Nothing to do here.
            return;
        }
        try
        {
            var r = await _api.ClearAsync(id);
            if (!r.Success)
            {
                _ = LogWarnAsync($"alarm.publish clear non-success for id={id}: {r.Error ?? "(no body)"}");
            }
        }
        catch (HttpRequestException ex)
        {
            _ = LogErrorAsync($"alarm.publish clear failed for id={id}", ex);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException
                                 || ex is TaskCanceledException
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

    private readonly record struct AlarmKey(string Title, string? TargetKey);

    /// <summary>Tracking record for a notification we've raised on the
    /// server. <c>Id</c> is null while the raise POST is in flight;
    /// the late-clear path checks <c>id is null</c> to know the
    /// response hasn't arrived yet. Once the server responds with a
    /// real id the record is replaced with the populated form.</summary>
    private readonly record struct RaisedEntry(string Path, string? Id);
}
