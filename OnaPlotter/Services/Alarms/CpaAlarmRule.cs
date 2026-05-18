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
    /// is the underway value - typically 0.3..0.5 nm so a developing
    /// crossing situation has time to read. When the boat is anchored
    /// (<see cref="NavigationData.AnchorActive"/> = the SignalK
    /// anchoralarm-plugin has a drop point set) the threshold narrows
    /// to the anchor's max swing radius - an alarm fires only when a
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

    public AlarmInfo? Check(AlarmEvaluationContext ctx)
    {
        // Harbor mode bundle: while the helm is entering / leaving a
        // busy harbour, every other vessel is a "near miss" so the
        // klaxon would fire continuously and the helm would silence
        // it - defeating the alarm. Suppressing the rule entirely
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
        // every vessel inside the loop - a busy harbour push has 200+
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

            // Single source of truth with the chart-overlay classifier
            // (AisPushService.BuildSnapshot). Previously this rule had
            // its own gate (`cpa.CpaNm >= cpaLimit` continue, `cpa.TcpaMin
            // > tcpaLimit` continue) which:
            //   1. drifted at the boundary - alarm used '>=' on the cpa
            //      side while ClassifyThreat uses strict '>', so a CPA
            //      hit exactly at the threshold radius classified as a
            //      threat on the chart but DIDN'T fire the audible alarm.
            //   2. didn't apply the current-distance ring gate, so the
            //      klaxon could fire for a vessel 5 nm away with a
            //      marginal closing track even though the chart-overlay's
            //      threat ring had already classified it as None.
            // Helm-feedback equivalent: "the X is gone but the alarm
            // still rings" - now they agree by construction.
            //
            // CurrentDistanceNm is a free byproduct of the projection
            // inside Cpa.Compute (see Cpa.Result XML doc); we used to
            // re-run a haversine here per vessel.
            var threat = Cpa.ClassifyThreat(
                cpa.Value.CpaNm, cpa.Value.TcpaMin,
                cpa.Value.CurrentDistanceNm,
                cpaLimit, tcpaLimit,
                v.IsBuddy);
            if (threat == Cpa.Threat.None) continue;

            // CPA threshold tripped. Now (and only now) compute the
            // COLREGS classification for the alarm banner suffix.
            // Previously this trig + bearing math ran for EVERY
            // vessel in the loop and the result was discarded for
            // every vessel that didn't trip the threshold. On a
            // 200-vessel harbour at 1 Hz that was 200 wasted
            // Classify calls/sec; the helm typically has 1-3
            // simultaneous CPA hits at most.
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
                data.CourseOverGround.Value, data.SpeedOverGround.Value,
                v.Latitude.Value, v.Longitude.Value,
                v.CourseOverGround.Value, v.SpeedOverGround.Value,
                ownType, tgtType);
            string? colregsShort = Colregs.ShortLabel(colregs.Category);
            string? colregsRole = Colregs.RoleLabel(colregs.Role);
            if (colregsShort is not null && colregsRole is not null)
                suffix = $" - {colregsShort}, {colregsRole}";
            else if (colregsShort is not null)
                suffix = $" - {colregsShort}";
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
