using OnaPlotter.Models;

namespace OnaPlotter.Services.ServerNotifications;

/// <summary>
/// Singleton store of currently-active alarm-class notifications,
/// keyed by full path. Two write surfaces feed it:
/// <list type="bullet">
///   <item>SignalkClient parses notifications.* WS deltas and pushes
///   them here via <see cref="Apply"/> (server-emitted entries).</item>
///   <item>MobService synthesises a local notifications.mob.&lt;localId&gt;
///   entry on raise so the alarm pipeline + chart marker fire
///   immediately, even when the SK server is unreachable. The local
///   synthetic is removed when the server's WS echo of the same MOB
///   lands at notifications.mob.&lt;serverId&gt; (reconciliation lives in
///   <see cref="OnaPlotter.Services.Mob.MobService"/>).</item>
/// </list>
/// The store does NOT distinguish between the two surfaces in its
/// data model -- the alarm pipeline + the
/// <see cref="ServerNotificationsAlarmRule"/> consume it uniformly.
/// If a future caller cares about provenance ("which entries came
/// from the server?"), add an <c>Origin</c> field to
/// <see cref="ServerNotification"/> rather than a parallel store.
/// <para>
/// The store does NOT generate alarms directly. It's a transient
/// data layer; the rule pipeline owns user-visible UX (banner,
/// snooze, dismiss cooldown).
/// </para>
/// </summary>
public sealed class ServerNotificationStore
{
    /// <summary>Hard cap on active notifications. A buggy plugin
    /// publishing under unique paths (e.g. <c>notifications.junk.{seq}</c>
    /// on a counter) could otherwise grow this dictionary without bound
    /// on a long passage and OOM the WASM heap. 256 is a generous ceiling
    /// -- a vessel with every standard plugin armed simultaneously sees
    /// less than a dozen active paths in practice. When the cap is hit
    /// we refuse the new entry rather than evict an existing one; the
    /// next clean delta will overwrite the placeholder so a transient
    /// flood doesn't permanently mask real alarms.</summary>
    public const int MaxActiveNotifications = 256;

    private readonly Dictionary<string, ServerNotification> _byPath
        = new(StringComparer.Ordinal);

    /// <summary>Fires when <see cref="Apply"/> mutates the active set
    /// (a new path arms, an existing path re-arms with a different
    /// payload, or the call cleared an active path). Used by the MOB
    /// pipeline to reconcile a local synthetic against a server WS
    /// echo without coupling SignalkClient directly to MobService.
    /// Single-threaded WASM assumption: handlers run synchronously
    /// inside Apply, before the bool result propagates to the
    /// caller.</summary>
    public event Action<string>? OnPathChanged;

    /// <summary>Snapshot of the currently-armed notifications. Returns a
    /// shallow copy so iteration is safe against concurrent
    /// <see cref="Apply"/> calls (Blazor WASM is single-threaded but the
    /// copy keeps callers from accidentally relying on live-update
    /// semantics).</summary>
    public IReadOnlyCollection<ServerNotification> Active
    {
        get
        {
            var snap = new ServerNotification[_byPath.Count];
            int i = 0;
            foreach (var v in _byPath.Values) snap[i++] = v;
            return snap;
        }
    }

    /// <summary>Number of currently-armed notifications. Cheaper than
    /// <see cref="Active"/>.Count when you only need the count.</summary>
    public int Count => _byPath.Count;

    /// <summary>Apply a notification delta. Pass a non-null state to
    /// arm; pass null OR "normal"/"cleared" to clear. Re-arming an
    /// already-active path with a different message updates the entry
    /// in-place. Returns true when the active set changed (caller can
    /// fire an OnChanged event).
    /// <para>
    /// On a SignalK v2 server (≥ 2.21), the value carries an <paramref
    /// name="id"/> (stable UUID per notification) and a <paramref
    /// name="status"/> block (ack / silence flags). Both are null on
    /// older servers; the store still works -- only the v2 banner
    /// affordances (Acknowledge button, server-side ack-clears) are
    /// inert in that case.
    /// </para>
    /// <para>
    /// A v2 notification with <c>status.acknowledged = true</c> is
    /// treated as cleared from the active set. Server-shared ack means
    /// "some plotter has handled this"; once it lands the helm doesn't
    /// need to keep seeing the banner. The actual server state stays
    /// armed; if the same notification re-fires later (e.g. the
    /// underlying condition flips back on after the server's 60 s GC
    /// window) we'll see a fresh delta with <c>acknowledged = false</c>
    /// and rearm the entry.
    /// </para>
    /// </summary>
    public bool Apply(string path, string? state, string? message,
        string? id = null, NotificationStatus? status = null,
        double? latitude = null, double? longitude = null,
        DateTime? createdAt = null)
    {
        var severity = MapSeverity(state);
        if (severity is null)
        {
            bool removed = _byPath.Remove(path);
            if (removed) OnPathChanged?.Invoke(path);
            return removed;
        }
        // V2 server-side ack: drop from the active set so the alarm
        // pipeline auto-clears. Without this the banner would persist
        // until the underlying condition itself resolves, which
        // defeats the cross-plotter ack flow ("plotter A acks, plotter
        // B's banner stays up").
        //
        // Defense-in-depth: refuse to honour status.acknowledged=true
        // on emergency-state notifications. The SignalK v2 spec
        // mandates canAcknowledge=false for state="emergency"; a
        // spec-conformant server cannot produce "emergency + acked",
        // but a buggy or compromised plugin could. Silencing an MOB
        // / fire / collision banner because of a rogue ack flag is
        // exactly the kind of safety failure the spec was designed to
        // prevent. We trust the server's status block (Phase A flow)
        // EXCEPT when state=emergency, where we keep the banner up
        // regardless.
        if (status is { Acknowledged: true } && !IsEmergencyState(state))
        {
            bool removedAck = _byPath.Remove(path);
            if (removedAck) OnPathChanged?.Invoke(path);
            return removedAck;
        }
        // Hard cap on active set. See MaxActiveNotifications. Refuse
        // new paths past the cap; existing paths can still update
        // (re-arm with new severity, clear) so a real condition
        // trapped inside the cap window can still resolve.
        if (_byPath.Count >= MaxActiveNotifications && !_byPath.ContainsKey(path))
        {
            return false;
        }
        // Use the original-case state string in the record so callers
        // can render "emergency" vs "alarm" if they care; severity is
        // pre-mapped to the AlarmSeverity coarse bucket the banner
        // actually uses.
        var notif = new ServerNotification(
            path, state!, message, severity.Value, id, status, latitude, longitude, createdAt);
        if (_byPath.TryGetValue(path, out var existing) && existing == notif)
        {
            // Idempotent: same exact notification re-applied. No state
            // change, no caller event needed.
            return false;
        }
        _byPath[path] = notif;
        OnPathChanged?.Invoke(path);
        return true;
    }

    private static bool IsEmergencyState(string? state) =>
        string.Equals(state, "emergency", StringComparison.Ordinal);

    /// <summary>Remove a path explicitly. Used when a delta arrives with
    /// a JSON null value (server cleared the notification). Returns
    /// true when something was actually removed.</summary>
    public bool Clear(string path)
    {
        bool removed = _byPath.Remove(path);
        if (removed) OnPathChanged?.Invoke(path);
        return removed;
    }

    /// <summary>Drop everything. Used on websocket disconnect so a
    /// stale cached notification doesn't haunt the alarm stack while
    /// we're offline (server might already have cleared it but we
    /// can't see).</summary>
    public void Reset() => _byPath.Clear();

    /// <summary>Maps the SignalK state string to the coarse
    /// <see cref="AlarmSeverity"/> bucket. Returns null for
    /// "normal"/"cleared"/null so callers can use it as a clear-signal.
    /// Unknown states fail safe to Danger -- a genuine alarm with a
    /// typo'd state still surfaces rather than being silently dropped.</summary>
    public static AlarmSeverity? MapSeverity(string? state)
    {
        if (string.IsNullOrEmpty(state)) return null;
        return state switch
        {
            "normal" or "cleared" => null,
            "emergency" or "alarm" => AlarmSeverity.Danger,
            "warn" or "alert" => AlarmSeverity.Warn,
            _ => AlarmSeverity.Danger,
        };
    }
}
