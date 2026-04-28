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
/// On each event we diff the live <see cref="IAlarmManager.ActiveAlarms"/>
/// against our own <see cref="_raised"/> map of "we raised this id":
/// </para>
/// <list type="bullet">
/// <item>Active locally + not yet raised -> POST raise.</item>
/// <item>Previously raised + no longer active -> DELETE clear.</item>
/// <item>Active and already raised -> no-op (idempotent on the
/// server side: a re-raise overlays).</item>
/// </list>
/// <para>
/// Fail-soft: every API call is fire-and-forget, with failure
/// removing the path from <see cref="PublishedAlarmTracker"/> so a
/// retry can fire on the next OnAlarmsChanged event. A flaky link
/// to the server doesn't break the local banner -- it just delays
/// cross-plotter sync until the link recovers.
/// </para>
/// <para>
/// Server-emitted alarms (those carrying a non-null
/// <see cref="AlarmInfo.NotificationId"/>) are skipped: re-publishing
/// would create a feedback loop. The bridge rule on every connected
/// plotter (including this one) remains the source of truth for those.
/// </para>
/// </summary>
public sealed class AlarmPublisher : IAsyncDisposable
{
    private readonly IAlarmManager _manager;
    private readonly INotificationsApi _api;
    private readonly PublishedAlarmTracker _tracker;
    private readonly Dictionary<AlarmKey, RaisedEntry> _raised = [];
    private bool _disposed;

    public AlarmPublisher(IAlarmManager manager, INotificationsApi api,
        PublishedAlarmTracker tracker)
    {
        _manager = manager;
        _api = api;
        _tracker = tracker;
        _manager.OnAlarmsChanged += HandleAlarmsChanged;
    }

    /// <summary>Async-aware reaction to the alarm-manager's change
    /// event. Diffs active vs raised, fires raise/clear as needed.
    /// Each API call is fire-and-forget; the next OnAlarmsChanged
    /// reconciles state if a call fails.</summary>
    private void HandleAlarmsChanged()
    {
        if (_disposed) return;
        var active = _manager.ActiveAlarms;
        var activeKeys = new HashSet<AlarmKey>(active.Count);

        // Pass 1: raise newcomers. Skip alarms that already carry a
        // NotificationId (those came from the server already; re-
        // raising would cause a publish loop).
        foreach (var a in active)
        {
            if (a.NotificationId is not null) continue;
            if (!TryMapToPath(a, out var path)) continue;
            var key = new AlarmKey(a.Title, a.TargetKey);
            activeKeys.Add(key);
            if (_raised.ContainsKey(key)) continue;
            _ = TryRaiseAsync(a, path, key);
        }

        // Pass 2: clear ones that left the active set. Snapshot the
        // keys to avoid mutating the dictionary mid-iteration.
        var toClear = _raised
            .Where(kv => !activeKeys.Contains(kv.Key))
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
        foreach (var (key, raised) in toClear)
        {
            _raised.Remove(key);
            _tracker.Remove(raised.Path);
            _ = TryClearAsync(raised.Id);
        }
    }

    private async Task TryRaiseAsync(AlarmInfo a, string path, AlarmKey key)
    {
        // Reserve the slot synchronously so a second event before the
        // POST resolves doesn't double-fire. The placeholder id is
        // overwritten below when the server's response arrives. We
        // also tag the path as ours immediately so the bridge rule
        // suppresses the echo even before the id is finalised.
        _raised[key] = new RaisedEntry(path, RaisedEntry.PendingId);
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
        catch
        {
            // ResourceHttp swallows HttpRequestException internally; a
            // throw here is an unexpected client-side error (e.g. JSON
            // serialiser issue). Drop the optimistic state so the next
            // change retries.
            _raised.Remove(key);
            _tracker.Remove(path);
            return;
        }

        if (!r.Success || r.Value is null)
        {
            // Server rejected (404 on a pre-2.21 server, 5xx on a busy
            // one). Release the slot so the next OnAlarmsChanged can
            // try again; meanwhile the local banner still works.
            _raised.Remove(key);
            _tracker.Remove(path);
            return;
        }

        // Late-clear race: the alarm may have left the active set while
        // we were awaiting. If our entry was removed by HandleAlarmsChanged
        // in the meantime, fire the clear now -- otherwise the server
        // notification would linger until the 60s GC.
        if (!_raised.ContainsKey(key))
        {
            _ = TryClearAsync(r.Value);
            _tracker.Remove(path);
            return;
        }

        _raised[key] = new RaisedEntry(path, r.Value);
    }

    private async Task TryClearAsync(string id)
    {
        if (id == RaisedEntry.PendingId)
        {
            // Race: a clear arrived before the raise resolved. The
            // raise-completion path will issue the clear when it sees
            // the slot was already removed. Nothing to do here.
            return;
        }
        try
        {
            await _api.ClearAsync(id);
        }
        catch
        {
            // Best-effort. The server's 60s GC will clean up if we
            // never succeed; cross-plotter sync just degrades gracefully.
        }
    }

    /// <summary>Maps a client-side alarm to a SignalK notification path.
    /// Paths follow the SignalK convention so the consume-side bridge
    /// rule on other plotters can derive a sensible banner title via
    /// <see cref="ServerNotificationsAlarmRule.DeriveTitleAndDefault"/>.
    /// Returns false for alarm titles we don't republish (server-
    /// emitted SART/MOB, server-driven APPROACH).</summary>
    internal static bool TryMapToPath(AlarmInfo a, out string path)
    {
        path = a.Title switch
        {
            // Below-surface depth is the most common SK depth alarm
            // path; the bridge rule's "environment.depth.*" prefix
            // mapping derives Title="DEPTH" on receivers.
            "SHALLOW" => "notifications.environment.depth.belowSurface",
            // Per-target collision path; sanitise the target context so
            // the URL-segment is path-safe (replace ':' from MMSI URNs
            // with '_'; that yields a stable, derivable id per vessel).
            "CPA" => SanitisePerTargetPath("notifications.security.collision", a.TargetKey),
            // Wind shift latches; receiver bridge derives Title="WIND".
            "WIND SHIFT" => "notifications.environment.wind.shift",
            // Anchor drag + tide are siblings; both render under
            // Title="ANCHOR" on the receiver via the navigation.anchor
            // prefix mapping. The leaf (dragging vs tide) lets us
            // clear them independently.
            "ANCHOR DRAG" => "notifications.navigation.anchor.dragging",
            "ANCHOR TIDE" => "notifications.navigation.anchor.tide",
            // Deadman is a helm-attention sensor; "helm.deadman" falls
            // through to the bridge rule's leaf-segment fallback so
            // receivers see Title="DEADMAN".
            "DEADMAN" => "notifications.helm.deadman",
            _ => string.Empty,
        };
        // Skip publishing for titles we don't republish:
        //  - SART / MOB: server already feeds these directly from AIS.
        //  - APPROACH: signalk-server's course provider already emits
        //    notifications.navigation.course.* deltas; republishing
        //    would clash with the server's own.
        return !string.IsNullOrEmpty(path);
    }

    private static string SanitisePerTargetPath(string prefix, string? targetKey)
    {
        if (string.IsNullOrEmpty(targetKey)) return string.Empty;
        // Strip the SK "vessels." prefix so the path is rooted at the
        // notification namespace, not nested under the vessel context.
        var suffix = targetKey.StartsWith("vessels.", StringComparison.Ordinal)
            ? targetKey["vessels.".Length..]
            : targetKey;
        // SK paths use '.' as a separator; URN MMSI segments contain
        // ':' which is illegal. Replace with '_' so the path tokenises
        // cleanly server-side. '.' in the suffix is fine (extends the
        // hierarchy).
        return $"{prefix}.{suffix.Replace(':', '_')}";
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _manager.OnAlarmsChanged -= HandleAlarmsChanged;
        // Best-effort cleanup so a soft refresh / nav-away doesn't
        // leave stale entries on the server. The 60s GC would catch
        // them anyway, but ack flows on other plotters work better
        // when stale notifications don't linger.
        var snapshot = _raised.Values.ToList();
        _raised.Clear();
        foreach (var r in snapshot)
        {
            _tracker.Remove(r.Path);
            await TryClearAsync(r.Id);
        }
    }

    private readonly record struct AlarmKey(string Title, string? TargetKey);

    private readonly record struct RaisedEntry(string Path, string Id)
    {
        /// <summary>Sentinel id stored while a raise is in flight.
        /// HandleAlarmsChanged uses presence-in-dict to decide
        /// "raised vs not"; the late-clear path checks for this
        /// sentinel to know the response hasn't arrived yet.</summary>
        public const string PendingId = "_publisher_pending_";
    }
}
