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

        // Project to metres using an equirectangular patch at the midpoint.
        const double MetresPerDegLat = 111_320.0;
        double midLatRad = (lat1 + lat2) * 0.5 * Math.PI / 180.0;
        double metresPerDegLon = MetresPerDegLat * Math.Cos(midLatRad);

        double x2 = (lon2 - lon1) * metresPerDegLon;
        double y2 = (lat2 - lat1) * MetresPerDegLat;

        // Velocity components: COG is bearing from north, so Vx = sin(COG)*SOG.
        double c1 = cog1Rad.Value, c2 = cog2Rad.Value;
        double vx1 = Math.Sin(c1) * s1, vy1 = Math.Cos(c1) * s1;
        double vx2 = Math.Sin(c2) * s2, vy2 = Math.Cos(c2) * s2;

        // Relative position (own - target) and velocity.
        double dpx = -x2, dpy = -y2;
        double dvx = vx1 - vx2, dvy = vy1 - vy2;

        double a = dvx * dvx + dvy * dvy;

        // Parallel courses at same speed -> CPA is the current separation, now.
        if (a < 1e-6)
        {
            double now = Math.Sqrt(dpx * dpx + dpy * dpy);
            return new Result(now / 1852.0, 0);
        }

        // t that minimises |dp + dv*t|.
        double t = -(dpx * dvx + dpy * dvy) / a;
        if (t < 0) return null; // CPA already happened.

        double cx = dpx + dvx * t;
        double cy = dpy + dvy * t;
        double cpa = Math.Sqrt(cx * cx + cy * cy);

        return new Result(cpa / 1852.0, t / 60.0);
    }
}
