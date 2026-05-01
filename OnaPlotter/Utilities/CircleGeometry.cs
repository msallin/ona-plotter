namespace OnaPlotter.Utilities;

/// <summary>
/// Pure helpers for approximating geographic circles as closed polygon
/// rings. Used by <see cref="OnaPlotter.Services.Api.RegionApi"/> to
/// emit GeoJSON-compatible circle regions; any future rendering path
/// that needs a circle-as-polyline reuses the same builder.
/// <para>The geometry constants (metres-per-degree, default vertex
/// count) live here alongside the algorithm so callers consume the
/// pair as a coherent unit rather than reaching into RegionApi for
/// numbers that have nothing to do with HTTP.</para>
/// </summary>
public static class CircleGeometry
{
    /// <summary>Average metres per degree of latitude anywhere on the
    /// sphere. Close enough for the few-km circle approximations we
    /// emit; we're drawing a circle, not navigating by dead reckoning.</summary>
    public const double MetersPerDegLatitude = 111_320.0;

    /// <summary>Default vertex count for circle approximation. 32 is
    /// visually indistinguishable from a true circle at chart zooms up
    /// to ~100 m/px and is 64 floats on the wire, trivially cheap.</summary>
    public const int DefaultVertexCount = 32;

    /// <summary>
    /// Builds a closed linear ring approximating a circle of the given
    /// radius (metres) about a lat/lon centre. Output is GeoJSON order:
    /// each vertex is <c>[lon, lat]</c>, and the first vertex is
    /// repeated at the end to close the ring.
    /// </summary>
    /// <param name="lat">Centre latitude, degrees.</param>
    /// <param name="lon">Centre longitude, degrees.</param>
    /// <param name="radiusMeters">Circle radius, metres.</param>
    /// <param name="vertices">Vertex count. 32 is the practical default
    /// (see <see cref="DefaultVertexCount"/>). Must be >= 3 — fewer
    /// vertices either NPE the close-the-ring step (0) or produce a
    /// degenerate "circle" (1, 2) that no consumer wants. We throw
    /// rather than silently returning garbage.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when
    /// <paramref name="vertices"/> is below 3.</exception>
    public static double[][] BuildRing(double lat, double lon, double radiusMeters, int vertices)
    {
        if (vertices < 3)
            throw new ArgumentOutOfRangeException(nameof(vertices), vertices,
                "A circle ring needs at least 3 vertices.");

        double cosLat = Math.Cos(lat * Math.PI / 180.0);
        double metersPerDegLon = MetersPerDegLatitude * cosLat;
        // Pole safety: cos(lat) approaches 0 near the poles, so dividing
        // by metersPerDegLon would explode dLon. Floor at 1 m/deg means
        // the ring degenerates to a north-south line near the pole
        // rather than producing NaN coordinates.
        if (metersPerDegLon < 1) metersPerDegLon = 1;

        var ring = new double[vertices + 1][];
        for (int i = 0; i < vertices; i++)
        {
            double angle = 2 * Math.PI * i / vertices;
            double dLat = (radiusMeters * Math.Cos(angle)) / MetersPerDegLatitude;
            double dLon = (radiusMeters * Math.Sin(angle)) / metersPerDegLon;
            ring[i] = [lon + dLon, lat + dLat];
        }
        ring[vertices] = ring[0]; // close
        return ring;
    }
}
