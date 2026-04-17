namespace OnaPlotter.Models;

/// <summary>
/// A single active alarm shown in the top banner. Title is one of SHALLOW,
/// CPA, or WIND SHIFT today; new categories should add their own short label.
/// </summary>
/// <param name="TargetKey">Stable identifier (AIS context / MMSI) for
/// per-target snooze. Null for alarms that don't refer to a single vessel
/// (SHALLOW, WIND SHIFT, ...).</param>
public sealed record AlarmInfo(
    string Title,
    string Message,
    AlarmSeverity Severity,
    string? TargetKey = null);

public enum AlarmSeverity
{
    Warn,
    Danger,
}
