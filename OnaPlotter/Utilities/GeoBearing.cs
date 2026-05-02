namespace OnaPlotter.Utilities;

/// <summary>
/// Great-circle bearing from one geographic point to another. The
/// canonical formulation (Aviation Formulary V1.46) using atan2 over
/// the spherical-trig identity. Result is in radians, normalised to
/// <c>[0, 2pi)</c> clockwise from true north -- the same convention
/// SignalK uses for <c>navigation.anchor.bearingTrue</c>, so callers
/// that previously consumed that path can swap to this helper without
/// re-deriving the unit / sign convention.
///
/// <para>The anchor HUD card uses this to draw the "where is my
/// anchor" needle without depending on the SignalK plugin to compute
/// + publish the bearing. Same routine works for any A-to-B bearing
/// (AIS popup brg, route leg headings) -- a deliberate landing pad
/// for further JS-to-C# moves of pure-math helpers.</para>
///
/// <para>Pure function; no allocation. Safe to call per-tick at HUD
/// cadence -- the trigonometric ops are constant-time.</para>
/// </summary>
public static class GeoBearing
{
    private const double DegToRad = Math.PI / 180.0;
    private const double TwoPi = 2.0 * Math.PI;

    /// <summary>
    /// Initial true-north bearing FROM <paramref name="fromLat"/>/
    /// <paramref name="fromLon"/> TO <paramref name="toLat"/>/
    /// <paramref name="toLon"/>, in radians on <c>[0, 2pi)</c>.
    /// Returns null if either point lacks a coordinate (caller's
    /// nullability flow stays clean -- no sentinel NaN to guard
    /// against downstream).
    /// </summary>
    public static double? RadiansFromTo(
        double? fromLat, double? fromLon, double? toLat, double? toLon)
    {
        if (fromLat is not double aLat || fromLon is not double aLon ||
            toLat is not double bLat || toLon is not double bLon)
        {
            return null;
        }
        return RadiansFromTo(aLat, aLon, bLat, bLon);
    }

    /// <summary>
    /// Non-nullable overload -- caller already knows both points
    /// exist. Useful in test setups where the boundary check
    /// happens elsewhere.
    /// </summary>
    public static double RadiansFromTo(
        double fromLat, double fromLon, double toLat, double toLon)
    {
        double phi1 = fromLat * DegToRad;
        double phi2 = toLat * DegToRad;
        double dLambda = (toLon - fromLon) * DegToRad;

        double y = Math.Sin(dLambda) * Math.Cos(phi2);
        double x = Math.Cos(phi1) * Math.Sin(phi2) -
                   Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(dLambda);

        // atan2 returns (-pi, pi]; normalise into [0, 2pi) so the
        // value matches the SignalK navigation.*.bearingTrue
        // convention and the HUD's clockwise-from-north rotation
        // transform reads correctly without sign juggling.
        double bearing = Math.Atan2(y, x);
        if (bearing < 0) bearing += TwoPi;
        // Defensive: floating-point can land bearing exactly at
        // 2*pi when atan2 is called near the boundary; wrap to 0.
        if (bearing >= TwoPi) bearing -= TwoPi;
        return bearing;
    }
}
