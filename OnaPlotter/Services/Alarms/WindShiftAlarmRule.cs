using OnaPlotter.Models;

namespace OnaPlotter.Services.Alarms;

/// <summary>WIND SHIFT: TWD has moved more than the configured threshold
/// over the lookback window. Transient - latches on the banner until the
/// user dismisses, since the underlying condition (a large shift over X
/// minutes) is a point-in-time event, not a steady state.</summary>
public sealed class WindShiftAlarmRule : IAlarmRule
{
    public string Title => "WIND SHIFT";
    public int Priority => 300;    // below collision / grounding
    public bool AutoClear => false;

    private double? _anchorDeg;
    private DateTime _anchorAt = DateTime.MinValue;

    public AlarmInfo? Check(AlarmEvaluationContext ctx)
    {
        var twdRad = ctx.Data.WindDirectionTrue;
        if (twdRad is null) return null;

        double twdDeg = twdRad.Value * 180.0 / Math.PI;
        if (twdDeg < 0) twdDeg += 360;

        double lookback = ctx.Settings.WindShiftLookbackMinutes;

        // Update anchor on first sample OR when the anchor is older than
        // the lookback window. When we rotate the anchor, first evaluate
        // whether the old anchor produced a shift worth reporting.
        if (_anchorDeg is null || (ctx.Now - _anchorAt).TotalMinutes >= lookback)
        {
            AlarmInfo? alarm = null;
            if (_anchorDeg is not null)
            {
                double shift = Math.Abs(twdDeg - _anchorDeg.Value);
                if (shift > 180) shift = 360 - shift;
                if (shift > ctx.Settings.WindShiftAlarmThreshold)
                {
                    alarm = new AlarmInfo(
                        Title,
                        $"TWD shifted {shift:F0}\u00b0 in {lookback:F0} min",
                        AlarmSeverity.Warn);
                }
            }
            _anchorDeg = twdDeg;
            _anchorAt = ctx.Now;
            return alarm;
        }
        return null;
    }
}
