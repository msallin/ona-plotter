namespace OnaPlotter.Utilities;

/// <summary>
/// Pure helpers for relating an active SignalK course (next-point lat/lon)
/// back to a waypoint index inside a fetched route. The renderer (JS) splits
/// the route into "passed" and "planned" segments using this index, so the
/// lookup needs to be deterministic and side-effect free.
/// </summary>
public static class RouteProgress
{
    /// <summary>
    /// Returns the index of the route waypoint nearest to (lat, lon).
    /// Used to pin SignalK's next-point payload (which carries lat/lon
    /// but not an index) to a concrete vertex in the route coordinate
    /// list.
    /// </summary>
    /// <remarks>
    /// Coordinates are passed as <c>[lat, lon]</c> pairs to match the JS
    /// wire format. Distance is computed via an equirectangular projection
    /// centred at <paramref name="lat"/> -- accurate to a fraction of a
    /// percent for the typical 1-50 NM scale of a sailing route, and 30x
    /// faster than haversine. We only need a winning index, not a
    /// metre-accurate distance, so the projection error is irrelevant.
    /// </remarks>
    public static int FindClosestWaypointIndex(double[][] coords, double lat, double lon)
    {
        if (coords is null || coords.Length == 0) return 0;

        // Equirectangular: dx = (lon - lon0) * cos(lat0), dy = (lat - lat0).
        // We compare squared distances, so the constant scale factor cancels
        // out and there's no need to convert to metres.
        double cosLat = Math.Cos(lat * Math.PI / 180.0);

        int best = 0;
        double bestSq = double.PositiveInfinity;
        for (int i = 0; i < coords.Length; i++)
        {
            double[] p = coords[i];
            if (p is null || p.Length < 2) continue;
            double dy = p[0] - lat;
            double dx = (p[1] - lon) * cosLat;
            double sq = dx * dx + dy * dy;
            if (sq < bestSq) { bestSq = sq; best = i; }
        }
        return best;
    }
}
