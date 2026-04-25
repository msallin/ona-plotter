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
    /// Returns the closest point of approach, or null if the vessels are
    /// diverging (CPA is in the past) or the inputs are insufficient.
    /// Angles are in radians measured clockwise from true north.
    /// </summary>
    public static Result? Compute(
        double lat1, double lon1, double? cog1Rad, double? sog1Ms,
        double lat2, double lon2, double? cog2Rad, double? sog2Ms)
    {
        if (cog1Rad is null || sog1Ms is null || cog2Rad is null || sog2Ms is null)
            return null;

        double s1 = sog1Ms.Value, s2 = sog2Ms.Value;
        if (s1 < StationaryThresholdMs && s2 < StationaryThresholdMs) return null;

        // Guard against malformed inputs. NaN/Infinity propagates through the
        // whole calc and gives a plausible-looking but nonsense CPA.
        if (!double.IsFinite(lat1) || !double.IsFinite(lon1)
            || !double.IsFinite(lat2) || !double.IsFinite(lon2)
            || !double.IsFinite(s1)   || !double.IsFinite(s2)
            || !double.IsFinite(cog1Rad.Value) || !double.IsFinite(cog2Rad.Value))
            return null;

        // Project to metres using an equirectangular patch at the midpoint.
        const double MetresPerDegLat = 111_320.0;
        double midLatRad = (lat1 + lat2) * 0.5 * Math.PI / 180.0;
        double metresPerDegLon = MetresPerDegLat * Math.Cos(midLatRad);

        // Unwrap longitude so vessels straddling the antimeridian (one at
        // 179.9, the other at -179.9) show as ~20 km apart instead of
        // ~40 000 km apart. Without this the alarm never fires near the
        // dateline and the CPA geometry is nonsense for any Pacific
        // crossing. Normalised delta in (-180, 180].
        double dLonDeg = ((lon2 - lon1 + 540.0) % 360.0) - 180.0;

        double x2 = dLonDeg * metresPerDegLon;
        double y2 = (lat2 - lat1) * MetresPerDegLat;

        // Velocity components: COG is bearing from north, so Vx = sin(COG)*SOG.
        double c1 = cog1Rad.Value, c2 = cog2Rad.Value;
        double vx1 = Math.Sin(c1) * s1, vy1 = Math.Cos(c1) * s1;
        double vx2 = Math.Sin(c2) * s2, vy2 = Math.Cos(c2) * s2;

        // Relative position (own - target) and velocity.
        double dpx = -x2, dpy = -y2;
        double dvx = vx1 - vx2, dvy = vy1 - vy2;

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
    /// Threat severity for a single CPA hit, used to drive marker colour /
    /// danger-ring pulse / red-vs-amber crossing line on the chart. Pure
    /// classification, no rendering side-effects.
    /// </summary>
    public enum Threat { None, Warning, Danger }

    /// <summary>
    /// Maps a CPA result to a <see cref="Threat"/> level using the helm's
    /// configured guard-zone radius and lookahead. Buddies are exempted by
    /// the caller passing <paramref name="isBuddy"/> = true so a friend
    /// sailing close never paints the chart red.
    /// </summary>
    /// <param name="cpaNm">CPA distance, nautical miles. Null = no CPA.</param>
    /// <param name="tcpaMin">TCPA time, minutes. Null or non-positive = no closing.</param>
    /// <param name="guardZoneRadiusNm">Helm-configured red-band radius.</param>
    /// <param name="lookaheadMin">Helm-configured red-band lookahead.</param>
    /// <param name="warningFactor">Multiplier on radius+lookahead for the amber band (typically 2.0).</param>
    /// <param name="isBuddy">If true the result is always <see cref="Threat.None"/>.</param>
    public static Threat ClassifyThreat(
        double? cpaNm, double? tcpaMin,
        double guardZoneRadiusNm, double lookaheadMin,
        double warningFactor, bool isBuddy)
    {
        if (isBuddy) return Threat.None;
        if (cpaNm is null || tcpaMin is null || tcpaMin <= 0) return Threat.None;
        if (cpaNm < guardZoneRadiusNm && tcpaMin < lookaheadMin) return Threat.Danger;
        if (cpaNm < guardZoneRadiusNm * warningFactor
            && tcpaMin < lookaheadMin * warningFactor) return Threat.Warning;
        return Threat.None;
    }
}
