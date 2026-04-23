using OnaPlotter.Models;
using OnaPlotter.Utilities;

namespace OnaPlotter.Services.Alarms;

/// <summary>CPA: projects every AIS vessel's closest point of approach
/// and raises an alarm when one would enter the configured CPA radius
/// within the configured TCPA lookahead. Buddies, moored, and
/// near-stationary vessels are exempt; per-vessel snoozes are honoured
/// via the evaluation context. Auto-clears once no vessel matches.</summary>
public sealed class CpaAlarmRule : IAlarmRule
{
    public string Title => "CPA";
    public int Priority => 200;    // between grounding and wind shift
    public bool AutoClear => true;

    /// <summary>Target SOG floor (m/s ~ 1 kn) below which the vessel is
    /// treated as "parked": no CPA alarm regardless of geometry. This
    /// catches boats that just dropped anchor, ferries waiting for a
    /// berth, and fishing boats jogging on station -- all classic
    /// sources of false-positive CPA alarms in crowded harbours.
    ///
    /// Trade-off: own-boat heading straight into a stopped fishing
    /// vessel at 8 kn will also be silent here. The grounding / track
    /// rules cover the "point of land / mark in your path" case;
    /// avoiding an actual floating obstacle that's not moving is a
    /// lookout problem, not a CPA-math problem. The
    /// <see cref="MooredVesselTracker"/> already handles settled boats
    /// (~2 min dwell under 0.5 kn); this raises the instant filter to
    /// 1 kn so vessels below that never feed the projection at all.
    /// </summary>
    public const double TargetStationaryMs = 0.514;   // ~1 kn

    private readonly MooredVesselTracker _moored = new();

    public AlarmInfo? Check(AlarmEvaluationContext ctx)
    {
        var data = ctx.Data;
        if (data.Latitude is null || data.Longitude is null
            || data.CourseOverGround is null || data.SpeedOverGround is null)
            return null;

        double cpaLimit = ctx.Settings.CpaAlarmThreshold;
        double tcpaLimit = ctx.Settings.GuardZoneLookaheadMinutes;

        // Drop tracker state for vessels that have left AIS range so the
        // dict doesn't grow without bound over long sessions.
        _moored.Cleanup(ctx.Vessels.Select(v => v.Context).ToHashSet());

        foreach (var v in ctx.Vessels)
        {
            if (v.Latitude is null || v.Longitude is null
                || v.CourseOverGround is null || v.SpeedOverGround is null)
                continue;

            if (v.IsBuddy) continue;                          // friends, not threats
            if (v.SpeedOverGround.Value < TargetStationaryMs) continue;  // parked / anchored
            if (_moored.IsMoored(v, ctx.Now)) continue;       // settled boats (2 min dwell)
            if (ctx.IsSnoozed(v.Context)) continue;

            var cpa = Cpa.Compute(
                data.Latitude.Value, data.Longitude.Value,
                data.CourseOverGround, data.SpeedOverGround,
                v.Latitude.Value, v.Longitude.Value,
                v.CourseOverGround, v.SpeedOverGround);

            if (cpa is null) continue;
            if (cpa.Value.CpaNm >= cpaLimit) continue;
            if (cpa.Value.TcpaMin > tcpaLimit) continue;

            string name = v.Name ?? v.Mmsi ?? "vessel";
            return new AlarmInfo(
                Title: Title,
                Message: $"{name}: CPA {cpa.Value.CpaNm:F2}nm in {cpa.Value.TcpaMin:F0}min",
                Severity: AlarmSeverity.Danger,
                TargetKey: v.Context,
                TargetLabel: name,
                TimeToEventMinutes: cpa.Value.TcpaMin);
        }
        return null;
    }
}
