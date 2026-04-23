using OnaPlotter.Models;
using OnaPlotter.Utilities;

namespace OnaPlotter.Services.Alarms;

/// <summary>CPA: projects every AIS vessel's closest point of approach
/// and raises an alarm when one would enter the configured CPA radius
/// within the configured TCPA lookahead. Buddies, moored vessels (SOG
/// held under <see cref="MooredVesselTracker.MooredSpeedThresholdMs"/>
/// ~1 kn for <see cref="MooredVesselTracker.MooredHoldSeconds"/>), and
/// per-vessel snoozed targets are exempt. Auto-clears once no vessel
/// matches.</summary>
public sealed class CpaAlarmRule : IAlarmRule
{
    public string Title => "CPA";
    public int Priority => 200;    // between grounding and wind shift
    public bool AutoClear => true;

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
            if (_moored.IsMoored(v, ctx.Now)) continue;       // parked / anchored / holding
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
