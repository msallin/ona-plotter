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
public sealed record ServerNotification(
    string Path,
    string State,
    string? Message,
    AlarmSeverity Severity);
