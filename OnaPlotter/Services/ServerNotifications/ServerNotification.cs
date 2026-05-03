using OnaPlotter.Models;

namespace OnaPlotter.Services.ServerNotifications;

/// <summary>
/// One server-side SignalK notification. SignalK plugins (anchor watch,
/// depth, MOB, course-provider, etc.) publish these under
/// notifications.* paths. We store the parsed shape per path so the
/// alarm stack can mirror what the server has decided is currently
/// worth raising.
/// </summary>
/// <param name="Path">Full SignalK path including the "notifications." prefix
/// (e.g. "notifications.environment.depth.belowTransducer"). Used as the
/// dedup key in the store and as <see cref="AlarmInfo.TargetKey"/> when
/// surfaced as an alarm.</param>
/// <param name="State">SignalK state string as published by the plugin:
/// "emergency" | "alarm" | "warn" | "alert". Already mapped to
/// <see cref="Severity"/>; kept as the original string for diagnostics.</param>
/// <param name="Message">Optional human-readable message from the plugin.
/// Falls back to the path tail in the alarm banner when missing.</param>
/// <param name="Severity">Mapped from State: emergency/alarm = Danger,
/// warn/alert = Warn. Unrecognised states fail safe to Danger so a
/// genuine alarm with a typo'd state still surfaces.</param>
/// <param name="Id">SignalK v2 server-assigned UUID -- stable for the
/// lifetime of this notification across re-emits. Null on
/// pre-2.21 servers; the alarm pipeline falls back to the path-based
/// dedup key in that case.</param>
/// <param name="Status">Server-side ack / silence flags. Null on
/// pre-2.21 servers. When present, drives the Acknowledge / Silence
/// banner buttons and lets <see cref="ServerNotificationsAlarmRule"/>
/// suppress already-acknowledged notifications.</param>
/// <param name="Latitude">Optional latitude attached to the
/// notification value (SignalK v2 safety alarms -- MOB / fire /
/// collision -- carry a <c>position</c> block so the chart can
/// render a marker without the receiver having to look up the
/// helm's last fix). Null when the server didn't publish one.</param>
/// <param name="Longitude">Companion to <see cref="Latitude"/>;
/// always travels paired (both null or both non-null).</param>
/// <param name="CreatedAt">Server-stamped UTC time the notification
/// was raised. SignalK v2 notifications carry a <c>createdAt</c>
/// field on the value -- using it for the MOB chart-marker
/// timestamp (instead of each plotter's local clock at render time)
/// keeps every connected plotter in lockstep on "when did this
/// happen". Null on pre-v2 servers / synthetic locally-raised
/// entries that haven't reconciled yet.</param>
public sealed record ServerNotification(
    string Path,
    string State,
    string? Message,
    AlarmSeverity Severity,
    string? Id = null,
    NotificationStatus? Status = null,
    double? Latitude = null,
    double? Longitude = null,
    DateTime? CreatedAt = null);
