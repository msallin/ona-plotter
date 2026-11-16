using OnaPlotter.Models;
using OnaPlotter.Utilities;

namespace OnaPlotter.Services.Alarms;

/// <summary>CPA: projects every AIS vessel's closest point of approach
/// and raises an alarm when one would enter the configured CPA radius
/// within the configured TCPA lookahead. Buddies and per-vessel snoozed
/// targets are exempt. Moored / anchored / slow-dwelling targets (see
/// <see cref="MooredVesselTracker"/>) are exempt only while own ship is
/// itself below <see cref="OwnShipSlowSogMs"/>; targets not under
/// command, restricted in ability to manoeuvre, or aground are never
/// exempt. Emits one alarm per qualifying
/// target; each auto-clears once its vessel no longer matches.</summary>
public sealed class CpaAlarmRule : IAlarmRule
{
    public AlarmRuleId Id => AlarmRuleId.Cpa;
    public string Title => "CPA";

    /// <summary>Own SOG (m/s, 2 kn) below which moored / anchored
    /// neighbours stop raising CPA alarms. 2 kn is marina and mooring-
    /// field manoeuvring speed, where boats on their berths are expected
    /// within a few metres and an alarm per hull would train the helm to
    /// mute the klaxon. Above it own ship is making way, and a
    /// stationary hull on the track is a real collision risk.</summary>
    public const double OwnShipSlowSogMs = 2.0 * 0.514444;
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

    /// <summary>Effective alarm-tier CPA distance (nautical miles) for
    /// THIS tick. Wraps <see cref="Cpa.EffectiveCpaRadiusNm"/> so the
    /// alarm rule + the chart-side chip classifier
    /// (<c>AisLayerController</c>) agree on the number.
    /// <para>
    /// The user-configured <see cref="IAlarmThresholds.CpaAlarmNm"/>
    /// is the underway value (default 0.1 nm). When the boat is
    /// anchored (<see cref="NavigationData.AnchorActive"/> = the SignalK
    /// anchoralarm-plugin has a drop point set) the threshold narrows
    /// to the anchor's max swing radius - an alarm fires only when a
    /// vessel could enter the anchor circle, not on every passer-by.
    /// </para>
    /// </summary>
    public static double EffectiveAlarmCpaNm(AlarmEvaluationContext ctx) =>
        Cpa.EffectiveCpaRadiusNm(
            ctx.Settings.CpaAlarmNm,
            ctx.Data.AnchorActive,
            ctx.Data.AnchorMaxRadius);

    /// <summary>Effective awareness-tier CPA distance for this tick.
    /// Same anchor-narrowing as the alarm tier - at anchor both tiers
    /// collapse to the swing radius so the helm gets one
    /// "anything-entering-the-circle" band rather than awareness chips
    /// for every harmless passer-by.</summary>
    public static double EffectiveAwarenessCpaNm(AlarmEvaluationContext ctx) =>
        Cpa.EffectiveCpaRadiusNm(
            ctx.Settings.CpaAwarenessNm,
            ctx.Data.AnchorActive,
            ctx.Data.AnchorMaxRadius);

    /// <summary>Strips the <c>vessels.</c> prefix from a SK context and replaces every
    /// non-path-safe character with '_' so the resulting suffix can't
    /// extend the path hierarchy (an AIS URN with a stray '.' would
    /// otherwise let an attacker collide with another notification).
    /// Single string allocation via <see cref="string.Create{TState}(int, TState, System.Buffers.SpanAction{char, TState})"/>
    /// since the output length is known up front. Cold path (only on
    /// alarm publish) but the alloc shape stays tidy.</summary>
    private static string SanitisePerTargetPath(string prefix, string targetKey)
    {
        ReadOnlySpan<char> suffix = targetKey.StartsWith("vessels.", StringComparison.Ordinal)
            ? targetKey.AsSpan("vessels.".Length)
            : targetKey.AsSpan();
        // prefix + '.' + sanitised suffix - exact length known up front.
        int totalLen = prefix.Length + 1 + suffix.Length;
        return string.Create(totalLen, (prefix, targetKey, suffix.Length, suffixStart: targetKey.Length - suffix.Length),
            static (span, state) =>
            {
                var (p, k, suffixLen, suffixStart) = state;
                p.AsSpan().CopyTo(span);
                span[p.Length] = '.';
                var dest = span[(p.Length + 1)..];
                var src = k.AsSpan(suffixStart, suffixLen);
                for (int i = 0; i < src.Length; i++)
                {
                    char c = src[i];
                    dest[i] = (char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-') ? c : '_';
                }
            });
    }

    // Shared moored-vessel tracker. Registered as a singleton so the
    // alarm rule + harbour filter agree on dwell counters and the SK
    // navigation.state classification rules live in one place. A
    // second instance would let two consumers disagree on whether a
    // given vessel is moored.
    private readonly IMooredVesselTracker _moored;
    public CpaAlarmRule(IMooredVesselTracker moored) => _moored = moored;

    /// <summary>Most imminent qualifying target (lowest TCPA), or null.
    /// The manager uses <see cref="CheckMany"/>; this single-result view
    /// exists for the <see cref="IAlarmRule"/> contract.</summary>
    public AlarmInfo? Check(AlarmEvaluationContext ctx)
    {
        AlarmInfo? best = null;
        foreach (var a in CheckMany(ctx))
        {
            if (best is null || a.TimeToEventMinutes < best.TimeToEventMinutes)
                best = a;
        }
        return best;
    }

    /// <summary>One alarm per qualifying target. Each carries its own
    /// TargetKey and TCPA so the manager's (severity, TTI, priority)
    /// ordering surfaces the most imminent threat and a dismiss or
    /// snooze applies to that target alone.</summary>
    public IEnumerable<AlarmInfo> CheckMany(AlarmEvaluationContext ctx)
    {
        // Harbor mode bundle: while the helm is entering / leaving a
        // busy harbour, every other vessel is a "near miss" so the
        // klaxon would fire continuously and the helm would silence
        // it - defeating the alarm. Suppressing the rule entirely
        // (paired with hiding the CPA arcs JS-side) lets the helm
        // focus on the chart + immediate obstacles. Reset to false
        // on every page reload so a forgotten Harbor mode never
        // silently rides into open water.
        if (ctx.Settings.HarborMode) yield break;

        var data = ctx.Data;
        // True-north COG: AIS target COG is true, so own COG must be too
        // regardless of the helm's magnetic display preference.
        double? ownCog = data.CourseOverGroundTrueResolved;
        if (data.Latitude is null || data.Longitude is null
            || ownCog is null || data.SpeedOverGround is null)
            yield break;

        bool ownSlow = data.SpeedOverGround.Value < OwnShipSlowSogMs;
        double cpaAlarmLimit = EffectiveAlarmCpaNm(ctx);
        double tcpaAlarmLimit = ctx.Settings.TcpaAlarmMin;
        double cpaAwarenessLimit = EffectiveAwarenessCpaNm(ctx);
        double tcpaAwarenessLimit = ctx.Settings.TcpaAwarenessMin;

        // Pre-compute own-ship sin/cos of COG ONCE here rather than on
        // every vessel inside the loop - a busy harbour push has 200+
        // targets at ~3 Hz and the own-ship trig is invariant across
        // the loop. Null short-circuits the whole pass.
        var ownSnap = Cpa.PrecomputeOwn(
            data.Latitude.Value, data.Longitude.Value,
            ownCog, data.SpeedOverGround);
        if (ownSnap is null) yield break;

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
            // Always consult the tracker so its dwell clock keeps
            // running while own ship is fast; the exemption itself
            // only applies when we are manoeuvring slowly ourselves.
            bool moored = _moored.IsMoored(v, ctx.Now);
            if (moored && ownSlow
                && !MooredVesselTracker.IsRestrictedOrAground(v.NavigationState))
                continue;
            if (ctx.IsSnoozed(v.Context)) continue;

            // Start from where the target is now, not its last report.
            // A fix too old to extrapolate is stale: no CPA from it.
            if (AisDeadReckoning.PositionAt(v, ctx.Now) is not { } tgt) continue;

            var cpa = Cpa.Compute(
                ownSnap.Value,
                tgt.Latitude, tgt.Longitude,
                v.CourseOverGround, v.SpeedOverGround);

            if (cpa is null) continue;

            // Single source of truth with the chart-overlay classifier
            // (AisLayerController.BuildSnapshot). The audible alarm fires
            // ONLY on Threat.Alarm; awareness-tier hits paint the
            // chart but stay silent so a developing crossing situation
            // is visible without nagging the cockpit.
            var threat = Cpa.ClassifyThreat(
                cpa.Value.CpaNm, cpa.Value.TcpaMin,
                cpaAlarmLimit, tcpaAlarmLimit,
                cpaAwarenessLimit, tcpaAwarenessLimit,
                v.IsBuddy);
            if (threat != Cpa.Threat.Alarm) continue;

            // CPA threshold tripped. Only now compute the COLREGS
            // classification for the banner suffix: on a 200-vessel
            // harbour the helm typically has 1-3 simultaneous hits, so
            // classifying lazily skips ~200 Classify calls per tick.
            string name = v.Name ?? v.Mmsi ?? "vessel";
            // Compact countdown format matching the on-chart CPA
            // chip: "0.42nm T -5′" - prime glyph for minutes,
            // signed leading dash so it reads as "time-minus-N"
            // rather than "T plus N". Helm reads CPA distance +
            // time-to-encounter as one phrase.
            //
            // Append the COLREGS classification + role when the
            // encounter resolves into a rule - the helm reads the
            // banner exactly when a decision is needed and the
            // "Crossing - give way" hint shaves seconds off the
            // 'who turns?' lookup that otherwise lives in the AIS
            // popup. Indeterminate / no-role cases skip the suffix
            // so a parallel-course encounter doesn't get a
            // misleading "Indeterminate" appended.
            string suffix = "";
            // Plumb own + target propulsion category into the
            // classifier so Rule 18 priority resolves the role
            // ("power gives way to sail") on the alarm banner the
            // same way the AIS popup does. Helm sets own type via
            // IAppSettings.OwnVesselType ('sail' default / 'power');
            // target type derives from design.aisShipType.
            var ownType = ctx.Settings.OwnVesselType == "sail"
                ? Colregs.VesselType.Sail
                : Colregs.VesselType.Power;
            var tgtType = Colregs.FromAisShipType(v.ShipType);
            var colregs = Colregs.Classify(
                data.Latitude.Value, data.Longitude.Value,
                ownCog.Value, data.SpeedOverGround.Value,
                tgt.Latitude, tgt.Longitude,
                v.CourseOverGround.Value, v.SpeedOverGround.Value,
                ownType, tgtType);
            string? colregsShort = Colregs.ShortLabel(colregs.Category);
            string? colregsRole = Colregs.RoleLabel(colregs.Role);
            if (colregsShort is not null && colregsRole is not null)
                suffix = $" - {colregsShort}, {colregsRole}";
            else if (colregsShort is not null)
                suffix = $" - {colregsShort}";
            yield return new AlarmInfo(
                RuleId: Id,
                Title: Title,
                Message: $"{name}: CPA {cpa.Value.CpaNm:F2}nm T -{cpa.Value.TcpaMin:F0}′{suffix}",
                Severity: AlarmSeverity.Danger,
                TargetKey: v.Context,
                TargetLabel: name,
                TimeToEventMinutes: cpa.Value.TcpaMin);
        }
    }
}
