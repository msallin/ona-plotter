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

    /// <summary>CPA distance in nautical miles, TCPA time in minutes.</summary>
    public readonly record struct Result(double CpaNm, double TcpaMin);

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

        return new Result(cpa / 1852.0, t / 60.0);
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

    /// <summary>
    /// Effective CPA radius (nautical miles) given the helm's underway
    /// threshold + anchor state. When the SignalK anchoralarm plugin is
    /// active and has published a max-radius, the visible guard ring AND
    /// the chip-classifier both narrow to the anchor swing radius; the
    /// underway threshold (typically 0.3+ nm) generates "every passing
    /// vessel chips" noise on a stationary boat in a crowded anchorage.
    /// <para>Pure helper so the alarm rule (CpaAlarmRule), the chip
    /// snapshot path (AisPushService), and the ring rendering
    /// (Map.razor.PushGuardZoneAsync) all settle on one number. A
    /// previous bug had the rings narrow but the chips keep using
    /// the underway threshold - helm saw amber chips floating
    /// outside the visible rings, called it broken.</para>
    /// </summary>
    /// <param name="underwayNm">Helm-configured CPA radius
    /// (<c>IAppSettings.CpaAlarmThreshold</c>).</param>
    /// <param name="anchorActive">True when SK anchoralarm-plugin has
    /// a drop point set (<c>NavigationData.AnchorActive</c>).</param>
    /// <param name="anchorMaxRadiusM">SK-published max swing radius in
    /// metres (<c>NavigationData.AnchorMaxRadius</c>); null when the
    /// plugin hasn't pushed a value yet.</param>
    /// <summary>Default CPA threshold used when storage corruption / a
    /// schema migration leaves the helm-configured value as NaN or
    /// non-positive. Matches IAppSettings's default-on-fresh-install
    /// (0.5 nm) so a recovery from a bad localStorage entry doesn't
    /// silently disable CPA classification entirely.</summary>
    private const double DefaultCpaThresholdNm = 0.5;

    public static double EffectiveRadiusNm(
        double underwayNm, bool anchorActive, double? anchorMaxRadiusM)
    {
        // Storage corruption / schema-migration mishap can land
        // underwayNm as NaN / Infinity / non-positive. Without the
        // guard, Math.Min(NaN, anchorNm) = NaN and ClassifyThreat
        // then sees `cpaNm < NaN` = false on every vessel - the CPA
        // alarm + threat ring go DARK with no helm-visible signal.
        // Recover to the spec default rather than fail-silent.
        double safeUnderway = (double.IsFinite(underwayNm) && underwayNm > 0)
            ? underwayNm : DefaultCpaThresholdNm;
        if (!anchorActive) return safeUnderway;
        if (anchorMaxRadiusM is not double maxM) return safeUnderway;
        if (!double.IsFinite(maxM) || maxM <= 0) return safeUnderway;
        double anchorNm = maxM / 1852.0;
        return Math.Min(safeUnderway, anchorNm);
    }

    /// <summary>
    /// Threat severity for a single CPA hit, used to drive marker colour /
    /// danger-ring pulse / red-vs-amber crossing line on the chart. Pure
    /// classification, no rendering side-effects.
    /// </summary>
    public enum Threat { None, Warning, Danger }

    /// <summary>Outer (warning-band) ring multiplier. Hardcoded at 2× the
    /// helm-configured guard-zone radius. Previously a settings-exposed
    /// "warning factor" with default 2.0; helms reported the second knob
    /// as confusing (the visible warning ring already implied 2×) and the
    /// factor's only realistic value was always 2 anyway. The amber ring
    /// now tracks the guard zone deterministically.</summary>
    public const double OuterRingMultiplier = 2.0;

    /// <summary>
    /// Maps a CPA result to a <see cref="Threat"/> level using the helm's
    /// configured guard-zone radius + lookahead PLUS the vessel's CURRENT
    /// distance from own ship. The current-distance gate is what stops a
    /// vessel 5 nm away with a marginal closing track from drawing a long
    /// crossing line across the chart - helms read those as visual noise
    /// because the ship is well outside the displayed guard ring.
    ///
    /// <para>Severity is decided by the CURRENT distance ring:</para>
    /// <list type="bullet">
    ///   <item><b>Danger</b>: target is already inside the guard zone
    ///   AND the CPA math says it'll get closer.</item>
    ///   <item><b>Warning</b>: target is between the guard zone and
    ///   2× guard zone (the "outer ring") AND the CPA math says it'll
    ///   reach inside the guard zone within the lookahead window.</item>
    ///   <item><b>None</b>: target outside the outer ring, CPA is in the
    ///   past, target is not closing, target won't reach inside the
    ///   guard zone, or the lookahead has already elapsed.</item>
    /// </list>
    ///
    /// <para>Buddies are exempted by the caller passing <paramref name="isBuddy"/>
    /// = true so a friend sailing close never paints the chart red.</para>
    /// </summary>
    /// <param name="cpaNm">Projected CPA distance, nautical miles. Null = no CPA.</param>
    /// <param name="tcpaMin">TCPA time, minutes. Null or non-positive = no closing.</param>
    /// <param name="currentDistanceNm">Current distance from own ship to the
    /// target, nautical miles. The ring-membership gate.</param>
    /// <param name="guardZoneRadiusNm">Helm-configured guard-zone radius
    /// (<see cref="EffectiveRadiusNm"/> output).</param>
    /// <param name="lookaheadMin">Helm-configured lookahead window. CPA
    /// further out than this is treated as None even if a future approach
    /// would otherwise classify - sleep first.</param>
    /// <param name="isBuddy">If true the result is always <see cref="Threat.None"/>.</param>
    public static Threat ClassifyThreat(
        double? cpaNm, double? tcpaMin,
        double currentDistanceNm,
        double guardZoneRadiusNm, double lookaheadMin,
        bool isBuddy)
    {
        if (isBuddy) return Threat.None;
        if (cpaNm is null || tcpaMin is null || tcpaMin <= 0) return Threat.None;
        if (tcpaMin > lookaheadMin) return Threat.None;
        // The vessel must actually reach inside the guard zone to count.
        // A parallel-course pass at 1.5 nm with 1.5 nm cpa shouldn't draw
        // a crossing line - they're just sailing alongside.
        if (cpaNm > guardZoneRadiusNm) return Threat.None;
        // Current-distance ring gate. The outer ring is hardcoded at 2×
        // guard zone (matches the visible warning-band circle).
        double outerRingNm = guardZoneRadiusNm * OuterRingMultiplier;
        if (currentDistanceNm > outerRingNm) return Threat.None;

        return currentDistanceNm <= guardZoneRadiusNm
            ? Threat.Danger
            : Threat.Warning;
    }
}
