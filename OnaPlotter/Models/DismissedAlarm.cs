namespace OnaPlotter.Models;

/// <summary>A past alarm retained for the alarm-log drawer. Stored as a
/// ring buffer (last N) in <see cref="OnaPlotter.Services.IAlarmManager"/>.
/// Captures why the alarm left the active stack so the user can tell a
/// conscious dismiss from an auto-clear (CPA resolving itself when the
/// other vessel turns away) when reviewing after the fact.</summary>
public sealed record DismissedAlarm(
    AlarmInfo Alarm,
    DateTime At,
    DismissReason Reason);

public enum DismissReason
{
    /// <summary>User pressed the dismiss button on the banner.</summary>
    UserDismissed,

    /// <summary>User pressed snooze; the target is also tracked in
    /// <see cref="OnaPlotter.Services.IAlarmManager.SnoozedTargets"/>.</summary>
    UserSnoozed,

    /// <summary>Rule stopped matching and was auto-clearing
    /// (<see cref="OnaPlotter.Services.IAlarmRule.AutoClear"/>).</summary>
    AutoCleared,
}
