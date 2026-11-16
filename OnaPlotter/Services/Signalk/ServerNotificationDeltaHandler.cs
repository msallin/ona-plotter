using System.Text.Json;
using OnaPlotter.Models;
using OnaPlotter.Services.ServerNotifications;
using OnaPlotter.Utilities;

namespace OnaPlotter.Services.Signalk;

/// <summary>
/// Routes self-context <c>notifications.*</c> deltas into the
/// <see cref="ServerNotificationStore"/>. The SignalK value is one of:
/// <list type="bullet">
/// <item>Object: <c>{ "state": "alarm"|"warn"|..., "method": [...], "message": "..." }</c> (armed)</item>
/// <item>JSON null OR object with state="normal" (cleared)</item>
/// </list>
/// Unknown shapes (numbers, strings, arrays) fail safe to a clear - no
/// point flapping the alarm stack on a malformed message.
/// </summary>
internal sealed class ServerNotificationDeltaHandler
{
    private readonly ServerNotificationStore _store;

    public ServerNotificationDeltaHandler(ServerNotificationStore store)
    {
        _store = store;
    }

    /// <summary>Applies one notification value. Returns true when the
    /// store changed (caller fires OnDataChanged so AlarmManager
    /// re-evaluates promptly).</summary>
    public bool Route(string path, DeltaValue rawValue)
    {
        // JSON null on the value: server cleared the notification.
        if (rawValue.IsNull)
        {
            return _store.Clear(path);
        }

        // Bare bool true: rare legacy form ("we have a notification");
        // treat as armed. Numbers, strings, false: clear, so a malformed
        // delta doesn't latch.
        if (!rawValue.IsObject)
        {
            return rawValue.TryGetBool(out var armed) && armed
                ? _store.Apply(path, "alarm", null)
                : _store.Clear(path);
        }

        // A flat {latitude, longitude} object is still an object
        // without a state, i.e. armed; give it the same parse.
        if (!rawValue.TryGetElement(out var el))
        {
            el = rawValue.ToJsonElement();
        }

        // Object form: pull state + message. Missing state on a
        // present object treats as "alarm" (most plugins emit only
        // object-when-armed, omit-state-when-they-mean-it; safer to
        // surface than to drop).
        //
        // SignalK v2 (>= 2.21) enriches every notification value with
        // `id` (stable UUID) and `status` (silenced / acknowledged /
        // canSilence / canAcknowledge / canClear). Both are optional;
        // we read them when present and store them on the
        // ServerNotification so the alarm pipeline can drive
        // server-side ack and the banner can hide the Acknowledge
        // button when the server's PGN-derived flags say "no".
        string? state = ReadString(el, "state");
        string? message = ReadString(el, "message");
        string? id = ReadString(el, "id");
        NotificationStatus? status = null;
        if (el.TryGetProperty("status", out var statusEl)
            && statusEl.ValueKind == JsonValueKind.Object)
        {
            status = ParseNotificationStatus(statusEl);
        }
        // Optional position block on safety alarms (MOB / fire /
        // collision). Server may emit `null` if it had no fix at
        // trigger time; any shape without both coordinates means "no
        // position" so the chart marker layer simply doesn't draw one.
        double? lat = null, lon = null;
        if (el.TryGetProperty("position", out var posEl)
            && posEl.TryGetLatLon(out var pLat, out var pLon))
        {
            lat = pLat;
            lon = pLon;
        }
        // Optional createdAt: SignalK v2 stamps the value's raise time
        // as an ISO-8601 string. Use it instead of each plotter's local
        // clock at render time so a MOB shows the same "MOB HH:MM:SS"
        // on every connected helm. Servers without it fall back to the
        // local clock at the display layer.
        DateTime? createdAt = null;
        if (el.TryGetProperty("createdAt", out var createdEl)
            && createdEl.ValueKind == JsonValueKind.String
            && SignalkTimestamp.TryParseUtc(createdEl.GetString(), out var parsed))
        {
            createdAt = parsed;
        }
        // Missing-state-on-an-object: assume armed at "alarm" severity.
        // Clears require an explicit normal/cleared string OR a JSON
        // null payload.
        state ??= "alarm";
        return _store.Apply(path, state, message, id, status, lat, lon, createdAt);
    }

    /// <summary>Drops every cached notification. While disconnected we
    /// can't see the server clear one, and a stale "ANCHOR DRAGGING"
    /// banner from before the drop would mislead; the server
    /// re-publishes the active set on reconnect.</summary>
    public void Reset() => _store.Reset();

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>Parses a SignalK v2 notification <c>status</c> object.
    /// Every field defaults to false on a missing / non-bool entry -
    /// fail-safe semantics ("if I can't tell, assume the action is
    /// unsupported").</summary>
    private static NotificationStatus ParseNotificationStatus(JsonElement el)
    {
        return new NotificationStatus(
            Silenced: ReadBool(el, "silenced"),
            Acknowledged: ReadBool(el, "acknowledged"),
            CanSilence: ReadBool(el, "canSilence"),
            CanAcknowledge: ReadBool(el, "canAcknowledge"),
            CanClear: ReadBool(el, "canClear"));

        static bool ReadBool(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    }
}
