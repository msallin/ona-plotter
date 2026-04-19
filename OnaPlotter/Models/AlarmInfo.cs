namespace OnaPlotter.Models;

/// <summary>
/// A single active alarm shown in the top banner. Title is one of SHALLOW,
/// CPA, or WIND SHIFT today; new categories should add their own short label.
/// </summary>
/// <param name="TargetKey">Stable identifier (AIS context / MMSI) for
/// per-target snooze. Null for alarms that don't refer to a single vessel
/// (SHALLOW, WIND SHIFT, ...).</param>
/// <param name="TargetLabel">Human-readable name for the target ("MV Aurora",
/// "123456789"). Used for the snoozed-target chip. Falls back to TargetKey
/// when null.</param>
/// <param name="Snoozeable">False for life-safety alarms that must not be
/// silenced by a passing tap -- SART / MOB / EPIRB beacons, primarily. The
/// UI hides the Snooze button for these and the manager refuses snooze
/// requests. Defaults to true for every other alarm type.</param>
/// <param name="TimeToEventMinutes">Optional estimated minutes until the
/// predicted event. 0 means "happening now" (SHALLOW, SART). CPA uses
/// its TCPA. ANCHOR TIDE uses hours-to-LW. Null means time-irrelevant
/// (latched wind-shift notification). Used by the manager to order
/// same-severity alarms so the most time-critical one surfaces first.</param>
public sealed record AlarmInfo(
    string Title,
    string Message,
    AlarmSeverity Severity,
    string? TargetKey = null,
    string? TargetLabel = null,
    bool Snoozeable = true,
    double? TimeToEventMinutes = null);

public enum AlarmSeverity
{
    Warn,
    Danger,
}
