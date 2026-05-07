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
    /// Picks the active leg index (= index of the next waypoint we're
    /// heading to) for the route renderer. The server's
    /// <c>navigation.course.activeRoute.pointIndex</c> is authoritative
    /// when present - it's what the SK course engine has been
    /// tracking through every leg advance, so on page reload it
    /// correctly reflects the "already driven" portion of the route.
    /// Falls back to a lat/lon -> closest-waypoint lookup only when
    /// the server didn't ship pointIndex (older provider, mid-upgrade)
    /// AND the next-point coordinates have arrived. Returns null when
    /// nothing is known yet, so the caller can skip the JS dispatch
    /// rather than render the entire route as undriven.
    /// </summary>
    public static int? ResolveLegIndex(
        int? serverPointIndex,
        double[][]? coords,
        double? nextLat,
        double? nextLon)
    {
        if (serverPointIndex is int p && p >= 0) return p;
        if (coords is null || coords.Length == 0) return null;
        if (nextLat is double lat && nextLon is double lon)
        {
            return FindClosestWaypointIndex(coords, lat, lon);
        }
        return null;
    }

    /// <summary>
    /// Returns the index of the route waypoint nearest to (lat, lon).
    /// Used to pin SignalK's next-point payload (which carries lat/lon
    /// but not an index) to a concrete vertex in the route coordinate
    /// list. Prefer <see cref="ResolveLegIndex"/> in render code so the
    /// server's authoritative pointIndex wins when present.
    /// </summary>
    /// <remarks>
    /// Coordinates are passed as <c>[lat, lon]</c> pairs to match the JS
    /// wire format. Distance is computed via an equirectangular projection
    /// centred at <paramref name="lat"/> - accurate to a fraction of a
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

    /// <summary>
    /// Total leg-by-leg distance of the route in metres. Used by the
    /// HUD's "Route total" line to display passed / total NM in one
    /// chip rather than just the remaining distance the SignalK course
    /// API publishes. Haversine on a sphere of mean Earth radius;
    /// matches the JS-side formula in leafletInterop.js so the two
    /// numbers stay reconcilable.
    /// </summary>
    public static double TotalDistanceMeters(double[][] coords)
    {
        if (coords is null || coords.Length < 2) return 0;
        double sum = 0;
        for (int i = 1; i < coords.Length; i++)
        {
            var a = coords[i - 1];
            var b = coords[i];
            if (a is null || b is null || a.Length < 2 || b.Length < 2) continue;
            sum += HaversineMeters(a[0], a[1], b[0], b[1]);
        }
        return sum;
    }

    /// <summary>Great-circle distance in metres between two lat/lon
    /// points. Mean Earth radius 6,371,000 m - accurate to ~0.5%
    /// across the typical sailing scale.</summary>
    public static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6_371_000.0;
        double phi1 = lat1 * Math.PI / 180.0;
        double phi2 = lat2 * Math.PI / 180.0;
        double dPhi = (lat2 - lat1) * Math.PI / 180.0;
        double dLam = (lon2 - lon1) * Math.PI / 180.0;
        double a = Math.Sin(dPhi / 2) * Math.Sin(dPhi / 2)
            + Math.Cos(phi1) * Math.Cos(phi2)
            * Math.Sin(dLam / 2) * Math.Sin(dLam / 2);
        double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return R * c;
    }
}
