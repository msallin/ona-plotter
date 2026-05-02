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
///
/// <para>Null TWS handling: signalk-derived-data publishes
/// <c>environment.wind.speedTrue</c> = null on ticks where it can't
/// derive TWS (typically: SOG is missing or zero, so AWS-minus-boat-
/// motion can't be computed). That null is functionally identical to
/// "below threshold" -- the wind data is unreliable and the helm
/// asked the gate to suppress on unreliable data. We track whether
/// TWS has EVER been live this session: once we've seen a non-null
/// TWS, a later null is treated as below-threshold (suppressed).
/// First-call null (TWS path absent) preserves the bypass so installs
/// without TWS publishing don't silently lose every wind-shift alarm.
/// </para>
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
    /// <summary>Sticks to true on first non-null TWS observation.
    /// Distinguishes "server doesn't publish TWS at all" (always-null,
    /// bypass the gate) from "TWS was live but went null this tick"
    /// (becalmed / SOG dropped, suppress the alarm).</summary>
    private bool _anyTwsSeen;

    public AlarmInfo? Check(AlarmEvaluationContext ctx)
    {
        var twdRad = ctx.Data.WindDirectionTrue;
        if (twdRad is null) return null;

        // Light-wind gate. Only enforced when the user opted in (>0 kn).
        // A missing TWS path (never seen in this session) bypasses --
        // installs that don't publish TWS shouldn't silently lose all
        // wind-shift alarms. A null after we've seen TWS go live is
        // treated as below-threshold: the SK plugin emits null when it
        // can't derive TWS (no SOG, becalmed), which is exactly the
        // condition the gate is meant to suppress.
        var minTwsKn = ctx.Settings.WindShiftMinTrueWindSpeed;
        if (ctx.Data.WindSpeedTrue is double twsMsLive)
        {
            _anyTwsSeen = true;
            if (minTwsKn > 0)
            {
                double twsKn = twsMsLive * Format.MsToKnots;
                if (twsKn < minTwsKn)
                {
                    _anchorDeg = null;
                    _anchorAt = DateTime.MinValue;
                    return null;
                }
            }
        }
        else if (minTwsKn > 0 && _anyTwsSeen)
        {
            // TWS path is publishing nulls AFTER having been live --
            // treat as below-threshold so the helm doesn't get a
            // shift alarm fired on heading-noise TWD while becalmed.
            _anchorDeg = null;
            _anchorAt = DateTime.MinValue;
            return null;
        }

        double twdDeg = twdRad.Value * 180.0 / Math.PI;

        double lookback = ctx.Settings.WindShiftLookbackMinutes;

        // Update anchor on first sample OR when the anchor is older than
        // the lookback window. When we rotate the anchor, first evaluate
        // whether the old anchor produced a shift worth reporting.
        if (_anchorDeg is null || (ctx.Now - _anchorAt).TotalMinutes >= lookback)
        {
            AlarmInfo? alarm = null;
            if (_anchorDeg is not null)
            {
                double shift = ShortestArcDeg(twdDeg, _anchorDeg.Value);
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

    /// <summary>Shortest unsigned arc between two bearings, in degrees,
    /// in [0, 180]. Tolerates inputs outside [0, 360) (negative,
    /// &gt; 360, or NaN-from-bad-server-publish): the modulo-pair
    /// normalises BOTH operands before subtraction so the result is
    /// independent of how the SignalK server publishes the wrap-around.
    /// Worked example: a=350, b=10 -&gt; ShortestArcDeg=20 (the short way
    /// around, not 340 the long way); a=370, b=-5 -&gt; same 15.</summary>
    private static double ShortestArcDeg(double a, double b)
    {
        if (!double.IsFinite(a) || !double.IsFinite(b)) return 0;
        double diff = ((a - b) % 360.0 + 540.0) % 360.0 - 180.0;
        return Math.Abs(diff);
    }
}
