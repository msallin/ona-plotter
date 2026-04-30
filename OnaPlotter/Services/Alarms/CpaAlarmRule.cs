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

    /// <summary>Per-target collision path. The bridge rule on every
    /// other plotter derives Title="COLLISION" from the
    /// <c>security.collision.*</c> prefix mapping. The vessel-context
    /// suffix is sanitised so an AIS-supplied URN can't smuggle path
    /// hierarchy past the <c>notifications.security.collision.</c>
    /// namespace (PARA-002).</summary>
    public string? GetPublishPath(AlarmInfo alarm)
    {
        if (string.IsNullOrEmpty(alarm.TargetKey)) return null;
        return SanitisePerTargetPath(
            "notifications.security.collision", alarm.TargetKey);
    }

    /// <summary>Effective CPA radius for THIS tick (nautical miles).
    /// The user-configured <see cref="IAlarmThresholds.CpaAlarmThreshold"/>
    /// is the underway value -- typically 0.3..0.5 nm so a developing
    /// crossing situation has time to read.
    /// <para>
    /// When the boat is anchored (<see cref="NavigationData.AnchorActive"/>
    /// = the SignalK anchoralarm-plugin has a drop point set) the
    /// underway threshold is wildly inappropriate: every passing
    /// vessel inside 0.3 nm of a stationary boat fires a CPA alarm
    /// even when the actual approach distance is hundreds of metres.
    /// We narrow the threshold to the anchor's max swing radius
    /// (<c>data.AnchorMaxRadius</c>, metres -&gt; nm) so an alarm
    /// fires only when a vessel could enter the anchor circle.
    /// </para>
    /// <para>
    /// Falls through to the underway threshold when:
    ///  - anchor isn't active,
    ///  - the radius hasn't arrived yet (server race),
    ///  - the anchor radius is somehow LARGER than the underway
    ///    threshold (the user wants the more cautious of the two,
    ///    which is the smaller).
    /// </para>
    /// </summary>
    public static double EffectiveCpaRadiusNm(AlarmEvaluationContext ctx)
    {
        double underway = ctx.Settings.CpaAlarmThreshold;
        if (!ctx.Data.AnchorActive) return underway;
        if (ctx.Data.AnchorMaxRadius is not double maxM) return underway;
        if (!double.IsFinite(maxM) || maxM <= 0) return underway;
        double anchorNm = maxM / 1852.0;
        return Math.Min(underway, anchorNm);
    }

    /// <summary>Mirrors the publisher's previous private helper. Strips
    /// the <c>vessels.</c> prefix from a SK context and replaces every
    /// non-path-safe character with '_' so the resulting suffix can't
    /// extend the path hierarchy (an AIS URN with a stray '.' would
    /// otherwise let an attacker collide with another notification).</summary>
    private static string SanitisePerTargetPath(string prefix, string targetKey)
    {
        var suffix = targetKey.StartsWith("vessels.", StringComparison.Ordinal)
            ? targetKey["vessels.".Length..]
            : targetKey;
        var sb = new System.Text.StringBuilder(suffix.Length);
        foreach (var c in suffix)
        {
            if (char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-')
                sb.Append(c);
            else
                sb.Append('_');
        }
        return $"{prefix}.{sb.ToString()}";
    }

    // Shared moored-vessel tracker. Was previously instantiated locally
    // here AND in Map.razor's PushAisTargets, so the two paths kept
    // separate dwell counters and could disagree on whether a given
    // vessel was moored. One registered service means alarm + harbour
    // filter share a single source of truth and the SK navigation.state
    // rules below only need to live in one place.
    private readonly IMooredVesselTracker _moored;
    public CpaAlarmRule(IMooredVesselTracker moored) => _moored = moored;

    public AlarmInfo? Check(AlarmEvaluationContext ctx)
    {
        // Harbor mode bundle: while the helm is entering / leaving a
        // busy harbour, every other vessel is a "near miss" so the
        // klaxon would fire continuously and the helm would silence
        // it -- defeating the alarm. Suppressing the rule entirely
        // (paired with hiding the CPA arcs JS-side) lets the helm
        // focus on the chart + immediate obstacles. Reset to false
        // on every page reload so a forgotten Harbor mode never
        // silently rides into open water.
        if (ctx.Settings.HarborMode) return null;

        var data = ctx.Data;
        if (data.Latitude is null || data.Longitude is null
            || data.CourseOverGround is null || data.SpeedOverGround is null)
            return null;

        double cpaLimit = EffectiveCpaRadiusNm(ctx);
        double tcpaLimit = ctx.Settings.GuardZoneLookaheadMinutes;

        // Drop tracker state for vessels that have left AIS range so the
        // dict doesn't grow without bound over long sessions. Vessels-
        // collection overload skips the HashSet build entirely when no
        // dwellers are tracked (the typical open-water tick).
        _moored.Cleanup(ctx.Vessels);

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
