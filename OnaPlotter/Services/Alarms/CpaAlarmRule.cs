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
    /// Wraps the shared <see cref="Cpa.EffectiveRadiusNm"/> helper so
    /// the alarm rule, the chart-side chip classifier
    /// (<c>AisPushService</c>), and the visible guard-ring renderer
    /// (<c>Map.razor.PushGuardZoneAsync</c>) all settle on one number.
    /// <para>
    /// The user-configured <see cref="IAlarmThresholds.CpaAlarmThreshold"/>
    /// is the underway value -- typically 0.3..0.5 nm so a developing
    /// crossing situation has time to read. When the boat is anchored
    /// (<see cref="NavigationData.AnchorActive"/> = the SignalK
    /// anchoralarm-plugin has a drop point set) the threshold narrows
    /// to the anchor's max swing radius -- an alarm fires only when a
    /// vessel could enter the anchor circle, not on every passer-by.
    /// </para>
    /// </summary>
    public static double EffectiveCpaRadiusNm(AlarmEvaluationContext ctx) =>
        Cpa.EffectiveRadiusNm(
            ctx.Settings.CpaAlarmThreshold,
            ctx.Data.AnchorActive,
            ctx.Data.AnchorMaxRadius);

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

        // Pre-compute own-ship sin/cos of COG ONCE here rather than on
        // every vessel inside the loop -- a busy harbour push has 200+
        // targets at ~3 Hz and the own-ship trig is invariant across
        // the loop. Null short-circuits the whole pass.
        var ownSnap = Cpa.PrecomputeOwn(
            data.Latitude.Value, data.Longitude.Value,
            data.CourseOverGround, data.SpeedOverGround);
        if (ownSnap is null) return null;

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
                ownSnap.Value,
                v.Latitude.Value, v.Longitude.Value,
                v.CourseOverGround, v.SpeedOverGround);

            if (cpa is null) continue;
            if (cpa.Value.CpaNm >= cpaLimit) continue;
            if (cpa.Value.TcpaMin > tcpaLimit) continue;

            string name = v.Name ?? v.Mmsi ?? "vessel";
            // Compact countdown format matching the on-chart CPA
            // chip: "0.42nm T -5′" -- prime glyph for minutes,
            // signed leading dash so it reads as "time-minus-N"
            // rather than "T plus N". Helm reads CPA distance +
            // time-to-encounter as one phrase.
            //
            // Append the COLREGS classification + role when the
            // encounter resolves into a rule -- the helm reads the
            // banner exactly when a decision is needed and the
            // "Crossing -- give way" hint shaves seconds off the
            // 'who turns?' lookup that otherwise lives in the AIS
            // popup. Indeterminate / no-role cases skip the suffix
            // so a parallel-course encounter doesn't get a
            // misleading "Indeterminate" appended.
            string suffix = "";
            // Plumb own + target propulsion category into the
            // classifier so Rule 18 priority resolves the role
            // ("power gives way to sail") on the alarm banner the
            // same way the AIS popup does. Helm sets own type via
            // IAppSettings.OwnVesselType ('power' default / 'sail');
            // target type derives from design.aisShipType.
            var ownType = ctx.Settings.OwnVesselType == "sail"
                ? Colregs.VesselType.Sail
                : Colregs.VesselType.Power;
            var tgtType = Colregs.FromAisShipType(v.ShipType);
            var colregs = Colregs.Classify(
                data.Latitude.Value, data.Longitude.Value,
                data.CourseOverGround.Value, data.SpeedOverGround.Value,
                v.Latitude.Value, v.Longitude.Value,
                v.CourseOverGround.Value, v.SpeedOverGround.Value,
                ownType, tgtType);
            string? colregsShort = Colregs.ShortLabel(colregs.Category);
            string? colregsRole = Colregs.RoleLabel(colregs.Role);
            if (colregsShort is not null && colregsRole is not null)
                suffix = $" -- {colregsShort}, {colregsRole}";
            else if (colregsShort is not null)
                suffix = $" -- {colregsShort}";
            return new AlarmInfo(
                Title: Title,
                Message: $"{name}: CPA {cpa.Value.CpaNm:F2}nm T -{cpa.Value.TcpaMin:F0}′{suffix}",
                Severity: AlarmSeverity.Danger,
                TargetKey: v.Context,
                TargetLabel: name,
                TimeToEventMinutes: cpa.Value.TcpaMin);
        }
        return null;
    }
}
