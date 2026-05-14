namespace OnaPlotter.Utilities;

/// <summary>
/// Polyline simplification via the Ramer-Douglas-Peucker algorithm.
/// Reduces a dense GPS track to a smaller set of waypoints that
/// preserve the route shape within a configurable perpendicular-
/// distance tolerance.
///
/// <para>Use case: a recorded trip with a sample every few seconds
/// becomes a route with hundreds of waypoints, which is wasteful
/// when re-sent to chartplotters or replayed. Running the points
/// through RDP at a sailing-realistic tolerance collapses straight
/// runs to two waypoints while preserving headland turns and channel
/// kinks.</para>
///
/// <para>Coordinates are projected to local metres via an equi-
/// rectangular projection anchored at the first point. At the
/// typical sailing scale (under a few hundred nm) the reported
/// perpendicular distance is within ~0.5 % of the true great-circle
/// value; for a 10 m tolerance the projection error is well below
/// 0.1 m, imperceptible against GPS noise.</para>
/// </summary>
public static class RouteSimplify
{
    /// <summary>Drop intermediate points whose perpendicular distance
    /// to the line between the surviving neighbouring points falls
    /// below <paramref name="toleranceMeters"/>. Endpoints are always
    /// preserved. Inputs of fewer than three points are returned
    /// unchanged. The input list is not mutated.</summary>
    /// <param name="latLon">[lat, lon] pairs in chronological order.</param>
    /// <param name="toleranceMeters">Perpendicular-distance tolerance
    /// in metres. Must be finite and non-negative.</param>
    public static List<double[]> Rdp(IReadOnlyList<double[]> latLon, double toleranceMeters)
    {
        if (!double.IsFinite(toleranceMeters) || toleranceMeters < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(toleranceMeters),
                toleranceMeters,
                "Tolerance must be a finite non-negative number of metres.");
        }

        int n = latLon.Count;
        if (n < 3) return [.. latLon];

        // Project lat/lon to a local Cartesian frame in metres so RDP's
        // perpendicular-distance check reduces to a planar cross-product.
        // Anchor at the first point's latitude; cos(lat) shrinks the lon
        // scale to match the meridional one at that parallel.
        double lat0 = latLon[0][0];
        double lon0 = latLon[0][1];
        double cosLat = Math.Cos(lat0 * GeoMath.DegToRad);
        double mPerDeg = GeoMath.EarthRadiusMeters * GeoMath.DegToRad;

        var xs = new double[n];
        var ys = new double[n];
        for (int i = 0; i < n; i++)
        {
            xs[i] = (latLon[i][1] - lon0) * cosLat * mPerDeg;
            ys[i] = (latLon[i][0] - lat0) * mPerDeg;
        }

        var keep = new bool[n];
        keep[0] = true;
        keep[n - 1] = true;
        SimplifySegment(xs, ys, keep, 0, n - 1, toleranceMeters * toleranceMeters);

        var result = new List<double[]>();
        for (int i = 0; i < n; i++)
        {
            if (keep[i]) result.Add(latLon[i]);
        }
        return result;
    }

    /// <summary>RDP recursion: find the intermediate point with the
    /// greatest perpendicular distance to the start-end chord; if it
    /// exceeds the tolerance, mark it kept and recurse on both halves.
    /// Otherwise every intermediate point gets dropped. Operates on
    /// projected coordinate arrays so the per-point cost is one cross-
    /// product plus one squared-length comparison.</summary>
    private static void SimplifySegment(
        double[] xs, double[] ys, bool[] keep, int start, int end, double tolSq)
    {
        if (end - start < 2) return;

        double sx = xs[start], sy = ys[start];
        double ex = xs[end], ey = ys[end];
        double dx = ex - sx, dy = ey - sy;
        double chordLenSq = dx * dx + dy * dy;

        double maxSq = -1;
        int maxIdx = -1;
        for (int i = start + 1; i < end; i++)
        {
            double px = xs[i], py = ys[i];
            double distSq;
            if (chordLenSq == 0)
            {
                // Degenerate chord (start == end): fall back to plain
                // Euclidean distance from the point to the shared end.
                double ddx = px - sx, ddy = py - sy;
                distSq = ddx * ddx + ddy * ddy;
            }
            else
            {
                // Perpendicular distance |p - s| projected onto the
                // normal of (e - s). 2D cross product magnitude /
                // chord length. We compare squared values to avoid
                // a sqrt per point.
                double cross = dx * (py - sy) - dy * (px - sx);
                distSq = (cross * cross) / chordLenSq;
            }
            if (distSq > maxSq)
            {
                maxSq = distSq;
                maxIdx = i;
            }
        }

        if (maxIdx < 0 || maxSq <= tolSq) return;
        keep[maxIdx] = true;
        SimplifySegment(xs, ys, keep, start, maxIdx, tolSq);
        SimplifySegment(xs, ys, keep, maxIdx, end, tolSq);
    }
}
