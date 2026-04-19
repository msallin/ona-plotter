using OnaPlotter.Models;

namespace OnaPlotter.Services.Alarms;

/// <summary>SHALLOW: depth below the user's configured alarm threshold.
/// Auto-clears when depth rises back above threshold.</summary>
public sealed class ShallowAlarmRule : IAlarmRule
{
    public string Title => "SHALLOW";
    public int Priority => 100;    // highest priority - grounding risk
    public bool AutoClear => true;

    public AlarmInfo? Check(AlarmEvaluationContext ctx)
    {
        var d = ctx.Data.Depth;
        if (d is null || d >= ctx.Settings.DepthAlarmThreshold) return null;
        // SHALLOW is happening now -- TTI=0 pins it above any pending
        // danger alarm (a CPA still 5min out).
        return new AlarmInfo(
            Title: Title,
            Message: $"Depth {d:F1}m < {ctx.Settings.DepthAlarmThreshold:F1}m",
            Severity: AlarmSeverity.Danger,
            TimeToEventMinutes: 0);
    }
}
