namespace OnaPlotter.Utilities;

/// <summary>
/// Closest Point of Approach for two constant-velocity surface vessels.
/// Uses a local equirectangular projection centred on the midpoint - accurate
/// to a few percent for vessel separations up to a few tens of nautical miles,
/// which covers every collision-avoidance scenario we care about.
/// </summary>
public static class Cpa
{
    /// <summary>A vessel with SOG below this (m/s, ~0.2 kn) is treated as stationary.</summary>
    private const double StationaryThresholdMs = 0.1;

    /// <summary>
    /// CPA result bundle.
    /// <list type="bullet">
    /// <item><description><b>CpaNm</b> - projected closest-approach
    /// distance in nautical miles.</description></item>
    /// <item><description><b>TcpaMin</b> - time to closest approach
    /// in minutes (always positive when present; <see cref="Compute(in OwnSnapshot, double, double, double?, double?)"/>
    /// returns null when CPA is in the past).</description></item>
    /// <item><description><b>CurrentDistanceNm</b> - separation
    /// between own and target RIGHT NOW, nautical miles. Free
    /// byproduct of the equirectangular projection inside Compute -
    /// the consumers (CpaAlarmRule, AisPushService.BuildSnapshot)
    /// previously each ran their own GeoMath.HaversineMeters call,
    /// so on a 200-vessel push at 3 Hz that was ~1200 redundant
    /// haversines/sec across both pipelines. Embedding it here
    /// drops both consumers to a single read.</description></item>
    /// </list>
    /// </summary>
    public readonly record struct Result(double CpaNm, double TcpaMin, double CurrentDistanceNm);

    /// <summary>
    /// Pre-computed own-vessel state for the per-target loop in
    /// <see cref="CpaAlarmRule"/> / <see cref="OnaPlotter.Services.Map.AisPushService"/>
    /// / Map.razor's CPA chip pass. The own-ship sin/cos of COG don't
    /// vary across the loop, so hoisting the trig out of the inner
    /// call avoids ~2 trig ops per AIS target per tick (200 vessels
    /// × ~3 Hz = ~1200 trig/sec on Pi 5 ARM).
    /// <para>Lat/Lon kept as separate fields rather than a tuple so
    /// the struct fits in two SSE registers; SOG is stored explicitly
    /// (rather than recovered from sqrt(Vx² + Vy²)) so the stationary
    /// guard in <see cref="Compute(in OwnSnapshot, double, double, double?, double?)"/>
    /// is a single comparison.</para>
    /// </summary>
    public readonly record struct OwnSnapshot(
        double Lat, double Lon,
        double Vx, double Vy,
        double SogMs);

    /// <summary>
    /// Returns the precomputed own-ship state used by the per-target
    /// <see cref="Compute(in OwnSnapshot, double, double, double?, double?)"/>
    /// overload. Returns null when own-ship inputs are missing or
    /// non-finite - the caller should skip the per-target loop
    /// entirely in that case.
    /// </summary>
    public static OwnSnapshot? PrecomputeOwn(
        double lat, double lon, double? cogRad, double? sogMs)
    {
        if (cogRad is null || sogMs is null) return null;
        if (!double.IsFinite(lat) || !double.IsFinite(lon)
            || !double.IsFinite(cogRad.Value) || !double.IsFinite(sogMs.Value))
            return null;
        double s = sogMs.Value;
        double c = cogRad.Value;
        return new OwnSnapshot(lat, lon, Math.Sin(c) * s, Math.Cos(c) * s, s);
    }

    /// <summary>
    /// Per-target CPA call against a pre-computed own-ship snapshot.
    /// Hoists the own-ship sin/cos out of the per-target loop.
    /// Behaviour matches the all-args
    /// <see cref="Compute(double, double, double?, double?, double, double, double?, double?)"/>
    /// overload exactly; the latter is now a thin wrapper that
    /// pre-computes via <see cref="PrecomputeOwn"/> and dispatches here.
    /// </summary>
    public static Result? Compute(
        in OwnSnapshot own, double lat2, double lon2,
        double? cog2Rad, double? sog2Ms)
    {
        if (cog2Rad is null || sog2Ms is null) return null;
        // Defence-in-depth: PrecomputeOwn already gates non-finite
        // inputs on construction, but a caller could hand-build an
        // OwnSnapshot in tests or via reflection. The struct is
        // public; treat it as untrusted input and guard explicitly.
        if (!double.IsFinite(own.Lat) || !double.IsFinite(own.Lon)
            || !double.IsFinite(own.Vx) || !double.IsFinite(own.Vy)
            || !double.IsFinite(own.SogMs))
            return null;

        double s1 = own.SogMs, s2 = sog2Ms.Value;
        if (s1 < StationaryThresholdMs && s2 < StationaryThresholdMs) return null;

        if (!double.IsFinite(lat2) || !double.IsFinite(lon2)
            || !double.IsFinite(s2) || !double.IsFinite(cog2Rad.Value))
            return null;

        // Project to metres using an equirectangular patch at the midpoint.
        const double MetresPerDegLat = 111_320.0;
        double midLatRad = (own.Lat + lat2) * 0.5 * Math.PI / 180.0;
        double metresPerDegLon = MetresPerDegLat * Math.Cos(midLatRad);

        // Unwrap longitude so vessels straddling the antimeridian (one at
        // 179.9, the other at -179.9) show as ~20 km apart instead of
        // ~40 000 km apart. Without this the alarm never fires near the
        // dateline and the CPA geometry is nonsense for any Pacific
        // crossing. Normalised delta in (-180, 180].
        double dLonDeg = ((lon2 - own.Lon + 540.0) % 360.0) - 180.0;

        double x2 = dLonDeg * metresPerDegLon;
        double y2 = (lat2 - own.Lat) * MetresPerDegLat;

        // Current separation falls out of the same projection - no need
        // for a second haversine call at the call site. Equirectangular
        // accuracy is good to a few percent at sub-100-nm separations,
        // which is exactly the regime collision-avoidance cares about.
        // Pinned in CpaTests against a haversine reference within 1%.
        double currentSepM = Math.Sqrt(x2 * x2 + y2 * y2);

        // Target velocity components: COG is bearing from north, so Vx = sin(COG)*SOG.
        double c2 = cog2Rad.Value;
        double vx2 = Math.Sin(c2) * s2, vy2 = Math.Cos(c2) * s2;

        // Relative position (own - target) and velocity.
        double dpx = -x2, dpy = -y2;
        double dvx = own.Vx - vx2, dvy = own.Vy - vy2;

        double a = dvx * dvx + dvy * dvy;

        // Parallel courses at same speed, or any other zero-closing pair.
        // Return null rather than fabricating TCPA=0 with the current
        // separation: the alarm rule would then fire instantly on two
        // boats peacefully cruising abreast at the same speed, which is
        // exactly the "false alarm" pattern we're trying to suppress.
        // Real closing geometry has non-zero relative velocity.
        if (a < 1e-6) return null;

        // t that minimises |dp + dv*t|.
        double t = -(dpx * dvx + dpy * dvy) / a;
        if (t < 0) return null; // CPA already happened.

        double cx = dpx + dvx * t;
        double cy = dpy + dvy * t;
        double cpa = Math.Sqrt(cx * cx + cy * cy);

        if (!double.IsFinite(cpa) || !double.IsFinite(t)) return null;

        return new Result(cpa / 1852.0, t / 60.0, currentSepM / 1852.0);
    }

    /// <summary>
    /// Returns the closest point of approach, or null if the vessels are
    /// diverging (CPA is in the past) or the inputs are insufficient.
    /// Angles are in radians measured clockwise from true north.
    /// <para>Single-call site convenience wrapper around
    /// <see cref="PrecomputeOwn"/> + the OwnSnapshot overload.
    /// Per-tick loops with N targets should call PrecomputeOwn ONCE
    /// outside the loop and the OwnSnapshot overload N times - this
    /// wrapper does the trig per call.</para>
    /// </summary>
    public static Result? Compute(
        double lat1, double lon1, double? cog1Rad, double? sog1Ms,
        double lat2, double lon2, double? cog2Rad, double? sog2Ms)
    {
        var own = PrecomputeOwn(lat1, lon1, cog1Rad, sog1Ms);
        if (own is null) return null;
        return Compute(own.Value, lat2, lon2, cog2Rad, sog2Ms);
    }

    /// <summary>Floor used when a helm-configured CPA value lands as
    /// NaN / negative / zero (storage corruption, schema migration
    /// mishap, hand-edited localStorage). Without the floor,
    /// ClassifyThreat's `cpa &lt;= NaN` test is false on every vessel
    /// and the whole tier goes dark with no helm-visible signal. A
    /// tiny positive number is the safe recovery: classification
    /// still runs, just very strictly.</summary>
    private const double SafeCpaFloorNm = 0.01;

    /// <summary>Same idea as <see cref="SafeCpaFloorNm"/>: a tiny
    /// positive TCPA floor so a corrupted lookahead can't collapse
    /// the whole classifier to None.</summary>
    private const double SafeTcpaFloorMin = 1.0;

    /// <summary>
    /// Anchor-aware effective CPA distance (nautical miles) for one
    /// classifier tier. When the SignalK anchoralarm plugin is active
    /// and has published a max swing radius, the configured tier
    /// distance narrows to the swing radius - the underway distance
    /// (0.1+ nm for alarm, 1.0+ nm for awareness) generates "every
    /// passing vessel chips / fires the klaxon" noise on a stationary
    /// boat in a crowded anchorage.
    /// <para>Used twice per evaluation tick: once for the alarm tier,
    /// once for awareness. Both call sites collapse to the anchor
    /// radius when anchored so the two tiers merge into a single
    /// "anything entering the swing circle" band - which is what an
    /// anchored helm actually wants.</para>
    /// </summary>
    /// <param name="configuredNm">Helm-configured tier distance
    /// (<c>IAppSettings.CpaAlarmNm</c> or <c>CpaAwarenessNm</c>).</param>
    /// <param name="anchorActive">True when SK anchoralarm-plugin has
    /// a drop point set (<c>NavigationData.AnchorActive</c>).</param>
    /// <param name="anchorMaxRadiusM">SK-published max swing radius in
    /// metres (<c>NavigationData.AnchorMaxRadius</c>); null when the
    /// plugin hasn't pushed a value yet.</param>
    public static double EffectiveCpaRadiusNm(
        double configuredNm, bool anchorActive, double? anchorMaxRadiusM)
    {
        double safe = (double.IsFinite(configuredNm) && configuredNm > 0)
            ? configuredNm : SafeCpaFloorNm;
        if (!anchorActive) return safe;
        if (anchorMaxRadiusM is not double maxM) return safe;
        if (!double.IsFinite(maxM) || maxM <= 0) return safe;
        double anchorNm = maxM / 1852.0;
        return Math.Min(safe, anchorNm);
    }

    /// <summary>
    /// Threat severity for a single CPA hit. Drives marker colour,
    /// klaxon trigger, crossing-line rendering. Two-tier model:
    /// <list type="bullet">
    ///   <item><b>None</b>: vessel not on a closing track, CPA in the
    ///   past, beyond the awareness window, or buddy.</item>
    ///   <item><b>Awareness</b>: silent on-chart attention - cross +
    ///   hover label, no audio. The "watch this one" band.</item>
    ///   <item><b>Alarm</b>: audible klaxon + red-blink marker +
    ///   always-on label. The "act now" band.</item>
    /// </list>
    /// </summary>
    public enum Threat { None, Awareness, Alarm }

    /// <summary>
    /// Maps a CPA result to a <see cref="Threat"/> level using the helm's
    /// two-tier thresholds. Awareness is the wider band (default 1.0 nm
    /// / 30 min); alarm is the inner strict band (default 0.1 nm / 30
    /// min). A target trips the alarm only when BOTH the alarm CPA and
    /// alarm TCPA limits are satisfied; otherwise it may still trip
    /// awareness if it's inside that wider band.
    ///
    /// <para>No current-distance gate: the awareness band is wide
    /// enough (1.0 nm) that a vessel 4 nm away converging fast is
    /// genuinely something the helm wants to see, which the previous
    /// 2× guard-zone gate would have suppressed.</para>
    ///
    /// <para>Buddies are exempted by the caller passing
    /// <paramref name="isBuddy"/> = true so a friend sailing close
    /// never paints the chart red.</para>
    /// </summary>
    /// <param name="cpaNm">Projected CPA distance, nm. Null = no CPA.</param>
    /// <param name="tcpaMin">TCPA time, minutes. Null or non-positive = no closing.</param>
    /// <param name="cpaAlarmNm">Alarm-tier CPA distance limit. From
    /// <see cref="EffectiveCpaRadiusNm"/> applied to
    /// <c>IAppSettings.CpaAlarmNm</c>.</param>
    /// <param name="tcpaAlarmMin">Alarm-tier TCPA window, minutes.
    /// <c>IAppSettings.TcpaAlarmMin</c>.</param>
    /// <param name="cpaAwarenessNm">Awareness-tier CPA distance limit.
    /// From <see cref="EffectiveCpaRadiusNm"/> applied to
    /// <c>IAppSettings.CpaAwarenessNm</c>.</param>
    /// <param name="tcpaAwarenessMin">Awareness-tier TCPA window,
    /// minutes. <c>IAppSettings.TcpaAwarenessMin</c>.</param>
    /// <param name="isBuddy">If true the result is always
    /// <see cref="Threat.None"/>.</param>
    public static Threat ClassifyThreat(
        double? cpaNm, double? tcpaMin,
        double cpaAlarmNm, double tcpaAlarmMin,
        double cpaAwarenessNm, double tcpaAwarenessMin,
        bool isBuddy)
    {
        if (isBuddy) return Threat.None;
        if (cpaNm is null || tcpaMin is null || tcpaMin <= 0) return Threat.None;

        // Defence against corrupted thresholds. The setters clamp on
        // write but a hand-crafted IAppSettings stub in tests or a
        // localStorage poke could land us here with NaN / negative
        // values; pin to the safe floor so the classifier still
        // returns something meaningful instead of None on every
        // vessel.
        double safeAlarmCpa = (double.IsFinite(cpaAlarmNm) && cpaAlarmNm > 0) ? cpaAlarmNm : SafeCpaFloorNm;
        double safeAlarmTcpa = (double.IsFinite(tcpaAlarmMin) && tcpaAlarmMin > 0) ? tcpaAlarmMin : SafeTcpaFloorMin;
        double safeAwarenessCpa = (double.IsFinite(cpaAwarenessNm) && cpaAwarenessNm > 0) ? cpaAwarenessNm : safeAlarmCpa;
        double safeAwarenessTcpa = (double.IsFinite(tcpaAwarenessMin) && tcpaAwarenessMin > 0) ? tcpaAwarenessMin : safeAlarmTcpa;
        // The invariant "awareness >= alarm" is enforced by the
        // AppSettingsService setters, but a stub could violate it.
        // Apply it here so the tiers never invert at the classifier.
        if (safeAwarenessCpa < safeAlarmCpa) safeAwarenessCpa = safeAlarmCpa;
        if (safeAwarenessTcpa < safeAlarmTcpa) safeAwarenessTcpa = safeAlarmTcpa;

        double cpa = cpaNm.Value;
        double tcpa = tcpaMin.Value;

        if (cpa <= safeAlarmCpa && tcpa <= safeAlarmTcpa) return Threat.Alarm;
        if (cpa <= safeAwarenessCpa && tcpa <= safeAwarenessTcpa) return Threat.Awareness;
        return Threat.None;
    }
}
