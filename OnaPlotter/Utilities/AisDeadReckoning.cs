using OnaPlotter.Models;

namespace OnaPlotter.Utilities;

/// <summary>
/// Dead-reckons an AIS / radar target from its last position fix to a
/// given instant using its reported SOG and COG. CPA math starts from
/// the target's position now; feeding it the last fix as-is under-states
/// how far a fast target has already travelled (a 20 kn ship moves
/// ~100 m between 10 s class A reports and ~1.8 km over a 3 min gap).
/// </summary>
public static class AisDeadReckoning
{
    /// <summary>Oldest fix still extrapolated. The slowest nominal AIS
    /// report interval is 3 min (class A at anchor, class B below 2 kn);
    /// 30 s of headroom covers relay latency so a healthy slow reporter
    /// isn't dropped between reports. Older than this the target has
    /// missed reports and its course may have changed: an extrapolated
    /// position would be a guess, so callers treat the target as stale.</summary>
    public static readonly TimeSpan MaxProjectionAge = TimeSpan.FromSeconds(210);

    /// <summary>A projected target position in decimal degrees.</summary>
    public readonly record struct Position(double Latitude, double Longitude);

    /// <summary>
    /// Target position at <paramref name="nowUtc"/>. Returns:
    /// <list type="bullet">
    /// <item>null when the target has no position, or its fix is older
    /// than <see cref="MaxProjectionAge"/> (stale: skip it).</item>
    /// <item>the fix unchanged when its time is unknown
    /// (<see cref="AisVessel.PositionUpdatedUtc"/> null), in the future
    /// (clock skew, clamped to zero age), or SOG / COG are missing or
    /// non-finite (nothing to extrapolate with).</item>
    /// <item>otherwise the fix advanced SOG x age along COG.</item>
    /// </list>
    /// </summary>
    public static Position? PositionAt(AisVessel v, DateTime nowUtc)
    {
        if (v.Latitude is not double lat || v.Longitude is not double lon) return null;
        if (v.PositionUpdatedUtc is not DateTime fixUtc) return new Position(lat, lon);

        var age = nowUtc - fixUtc;
        if (age > MaxProjectionAge) return null;
        if (age <= TimeSpan.Zero) return new Position(lat, lon);

        if (v.SpeedOverGround is not double sog || v.CourseOverGround is not double cog
            || !double.IsFinite(sog) || !double.IsFinite(cog) || sog <= 0)
            return new Position(lat, lon);

        var (pLat, pLon) = GeoMath.DestPoint(lat, lon, cog, sog * age.TotalSeconds);
        return new Position(pLat, pLon);
    }
}
