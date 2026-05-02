namespace OnaPlotter.Utilities;

/// <summary>
/// Pure spherical-geometry helpers used across the plotter: great-
/// circle distance, initial bearing, destination-point projection,
/// and the speed-vs-time look-ahead that produces COG vector
/// endpoints. Mirrors the JS surface in
/// <c>OnaPlotter/wwwroot/js/geoMath.js</c> so the same maths is
/// available to C# decision code (alarm rules, segmenter, polar
/// projection) without a JS round-trip.
///
/// <para>
/// Per the project rule (decisions in C#, JS as a thin renderer),
/// new callers should use these helpers; the JS copies stay because
/// some renderers (Leaflet tile-layer math, route-edit polyline
/// updates) need them locally to keep render paths fast.
/// </para>
///
/// <para>
/// Mean Earth radius 6,371,000 m is the WGS-84 mean-radius value;
/// great-circle distances accurate to ~0.5 % at the typical sailing
/// scale (under 500 nm; below 0.1 % under 50 nm). For higher
/// fidelity over very long passages the helmsman should reach for
/// Vincenty / GeographicLib; this module is intentionally simpler.
/// </para>
/// </summary>
public static class GeoMath
{
    /// <summary>Mean Earth radius in metres (WGS-84). Public so
    /// callers doing their own great-circle math share the same
    /// constant the rest of the code uses.</summary>
    public const double EarthRadiusMeters = 6_371_000.0;

    /// <summary>Degrees-to-radians conversion factor.</summary>
    public const double DegToRad = Math.PI / 180.0;

    /// <summary>Radians-to-degrees conversion factor.</summary>
    public const double RadToDeg = 180.0 / Math.PI;

    /// <summary>Default look-ahead window for COG vectors when the
    /// caller doesn't pass an explicit minutes value. Mirrors
    /// <c>VECTOR_MINUTES</c> in geoMath.js. The user-tunable
    /// per-vessel-class settings (OwnCogVectorMinutes /
    /// AisCogVectorMinutes) flow through the call site rather
    /// than being read from settings here.</summary>
    public const double DefaultVectorMinutes = 10.0;

    /// <summary>
    /// Great-circle distance in metres between two lat/lon points
    /// (decimal degrees). Haversine on a sphere of mean Earth radius.
    /// Same formula as the existing <c>RouteProgress.HaversineMeters</c>;
    /// kept as a separate entry-point so non-route code reads as
    /// "geometry helper" rather than "route progress helper".
    /// </summary>
    public static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        double phi1 = lat1 * DegToRad;
        double phi2 = lat2 * DegToRad;
        double dPhi = (lat2 - lat1) * DegToRad;
        double dLam = (lon2 - lon1) * DegToRad;
        double a = Math.Sin(dPhi / 2) * Math.Sin(dPhi / 2)
                 + Math.Cos(phi1) * Math.Cos(phi2)
                 * Math.Sin(dLam / 2) * Math.Sin(dLam / 2);
        double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return EarthRadiusMeters * c;
    }

    /// <summary>
    /// Initial great-circle bearing (degrees, 0..360, clockwise from
    /// true north) from point 1 to point 2. Decimal-degree inputs.
    /// Note: "initial" -- great-circle paths curve, so the bearing
    /// at the endpoint differs from the bearing at the start. For
    /// short legs the difference is negligible.
    /// </summary>
    public static double BearingDeg(double lat1, double lon1, double lat2, double lon2)
    {
        double dLon = (lon2 - lon1) * DegToRad;
        double phi1 = lat1 * DegToRad;
        double phi2 = lat2 * DegToRad;
        double y = Math.Sin(dLon) * Math.Cos(phi2);
        double x = Math.Cos(phi1) * Math.Sin(phi2)
                 - Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(dLon);
        return (Math.Atan2(y, x) * RadToDeg + 360) % 360;
    }

    /// <summary>
    /// Destination point reached from a starting position after
    /// travelling <paramref name="distMeters"/> on initial bearing
    /// <paramref name="bearingRad"/> radians. Returns
    /// (latitude, longitude) in decimal degrees.
    /// </summary>
    public static (double Lat, double Lon) DestPoint(
        double lat, double lon, double bearingRad, double distMeters)
    {
        double phi1 = lat * DegToRad;
        double lam1 = lon * DegToRad;
        double angDist = distMeters / EarthRadiusMeters;
        double sinPhi1 = Math.Sin(phi1);
        double cosPhi1 = Math.Cos(phi1);
        double sinD = Math.Sin(angDist);
        double cosD = Math.Cos(angDist);
        double phi2 = Math.Asin(sinPhi1 * cosD + cosPhi1 * sinD * Math.Cos(bearingRad));
        double lam2 = lam1 + Math.Atan2(
            Math.Sin(bearingRad) * sinD * cosPhi1,
            cosD - sinPhi1 * Math.Sin(phi2));
        return (phi2 * RadToDeg, lam2 * RadToDeg);
    }

    /// <summary>
    /// Course-vector endpoint: the position the boat would reach if
    /// it held its current SOG + COG for <paramref name="minutes"/>.
    /// Returns null when the speed is below the minimum the JS layer
    /// uses (0.1 m/s) -- a stationary or near-stationary boat
    /// produces a meaningless vector tip. Pass <c>null</c> for
    /// <paramref name="minutes"/> to use <see cref="DefaultVectorMinutes"/>;
    /// pass an explicit value (typically <c>OwnCogVectorMinutes</c> or
    /// <c>AisCogVectorMinutes</c>) to use the per-vessel-class horizon.
    /// </summary>
    public static (double Lat, double Lon)? VectorEnd(
        double lat, double lon, double cogRad, double sogMs, double? minutes = null)
    {
        // 0.1 m/s threshold mirrors geoMath.js -- below this the
        // vector renders as a hairline that doesn't communicate
        // direction to the helm. Anchored / drifting boats fall here.
        if (sogMs < 0.1) return null;
        double m = (minutes is double mm && double.IsFinite(mm) && mm > 0)
            ? mm
            : DefaultVectorMinutes;
        return DestPoint(lat, lon, cogRad, sogMs * m * 60.0);
    }
}

/// <summary>
/// Speed-vs-bucket lookup for track-segment grouping. The thresholds
/// drive trip-segmenter colouring (anchored / under-sail / over-7kn)
/// and the History playback's segment-class CSS variable, so a
/// helm-facing label always maps to the same numeric range whether
/// the renderer lives in C# (Stats / TrackSegmenter) or JS
/// (track polyline shading).
///
/// <para>
/// Mirrors <c>SPEED_BUCKETS</c> in <c>geoMath.js</c>. New callers in
/// C# should consume <see cref="Buckets"/> + <see cref="Bucket"/>
/// here; the JS copy stays so the existing render paths keep working.
/// </para>
/// </summary>
public static class SpeedBuckets
{
    /// <summary>Threshold table in metres-per-second (ascending).
    /// A SOG of B[i] or above lands in bucket i; below B[0] = 0
    /// lands in bucket 0. Public + read-only so callers can index
    /// their own colour / label tables off these without re-typing
    /// the cutoffs.</summary>
    public static readonly double[] Buckets = [0.0, 1.0, 2.0, 3.0, 5.0, 8.0];

    /// <summary>
    /// Return the bucket index (0..Buckets.Length-1) for a SOG in
    /// metres-per-second. <c>null</c> input returns 0 (mirrors the
    /// JS behaviour: a tick with no SOG is treated as anchored).
    /// </summary>
    public static int Bucket(double? sogMs)
    {
        if (sogMs is null) return 0;
        double v = sogMs.Value;
        // Iterate top-down so the first match is the highest
        // applicable bucket; a SOG of exactly 5 m/s belongs in
        // bucket 4 (>=5), not bucket 3 (>=3). Same iteration order
        // as the JS implementation.
        for (int i = Buckets.Length - 1; i >= 0; i--)
        {
            if (v >= Buckets[i]) return i;
        }
        return 0;
    }
}
