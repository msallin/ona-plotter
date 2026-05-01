using OnaPlotter.Models;
using OnaPlotter.Utilities;

namespace OnaPlotter.Services.Alarms;

/// <summary>WIND SHIFT: TWD has moved more than the configured threshold
/// over the lookback window. Transient -- latches on the banner until
/// the user dismisses, since the underlying condition (a large shift
/// over X minutes) is a point-in-time event, not a steady state.
///
/// Light-wind gate: when the user has set a minimum TWS and the live
/// reading is below it, the rule skips evaluation AND drops its anchor.
/// Reason: TWD is computed from AWS + heading + SOG; in light air the
/// heading / SOG noise dominates and a 40 deg "shift" between two
/// near-zero wind samples is not a tactical event. Dropping the anchor
/// means that when real wind returns, the next lookback window starts
/// fresh rather than measuring against a stale before-becalmed bearing.
/// </summary>
public sealed class WindShiftAlarmRule : IAlarmRule
{
    public string Title => "WIND SHIFT";
    public int Priority => 300;    // below collision / grounding
    public bool AutoClear => false;

    /// <summary>Cross-plotter publish path. Receivers derive
    /// Title="WIND" from the <c>environment.wind.*</c> prefix
    /// mapping.</summary>
    public string? GetPublishPath(AlarmInfo alarm) =>
        "notifications.environment.wind.shift";

    private double? _anchorDeg;
    private DateTime _anchorAt = DateTime.MinValue;

    public AlarmInfo? Check(AlarmEvaluationContext ctx)
    {
        var twdRad = ctx.Data.WindDirectionTrue;
        if (twdRad is null) return null;

        // Light-wind gate. Only enforced when the user opted in (>0 kn)
        // AND the server actually publishes TWS -- a missing TWS path
        // must not silently suppress every shift on installs that don't
        // emit environment.wind.speedTrue.
        var minTwsKn = ctx.Settings.WindShiftMinTrueWindSpeed;
        if (minTwsKn > 0 && ctx.Data.WindSpeedTrue is double twsMs)
        {
            double twsKn = twsMs * Format.MsToKnots;
            if (twsKn < minTwsKn)
            {
                _anchorDeg = null;
                _anchorAt = DateTime.MinValue;
                return null;
            }
        }

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
                        $"TWD shifted {shift:F0}° in {lookback:F0} min",
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
