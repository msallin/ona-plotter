using OnaPlotter.Models;

namespace OnaPlotter.Services.ServerNotifications;

/// <summary>
/// Singleton store of currently-active SignalK server notifications,
/// keyed by full path. SignalkClient parses notifications.* deltas
/// and pushes them here via <see cref="Apply"/>; the
/// <see cref="ServerNotificationsAlarmRule"/> reads the active set on
/// every Evaluate tick and emits one AlarmInfo per entry.
/// <para>
/// The store does NOT generate alarms directly. It's a transient
/// data layer; the rule pipeline owns user-visible UX (banner,
/// snooze, dismiss cooldown).
/// </para>
/// </summary>
public sealed class ServerNotificationStore
{
    private readonly Dictionary<string, ServerNotification> _byPath
        = new(StringComparer.Ordinal);

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
    /// fire an OnChanged event).</summary>
    public bool Apply(string path, string? state, string? message)
    {
        var severity = MapSeverity(state);
        if (severity is null)
        {
            return _byPath.Remove(path);
        }
        // Use the original-case state string in the record so callers
        // can render "emergency" vs "alarm" if they care; severity is
        // pre-mapped to the AlarmSeverity coarse bucket the banner
        // actually uses.
        var notif = new ServerNotification(path, state!, message, severity.Value);
        if (_byPath.TryGetValue(path, out var existing) && existing == notif)
        {
            // Idempotent: same exact notification re-applied. No state
            // change, no caller event needed.
            return false;
        }
        _byPath[path] = notif;
        return true;
    }

    /// <summary>Remove a path explicitly. Used when a delta arrives with
    /// a JSON null value (server cleared the notification). Returns
    /// true when something was actually removed.</summary>
    public bool Clear(string path) => _byPath.Remove(path);

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
