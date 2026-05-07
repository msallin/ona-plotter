namespace OnaPlotter.Utilities;

/// <summary>
/// Pure 2D point-in-polygon helper for SignalK regions. Used by the
/// hazard-region alarm rule to decide whether own-ship sits inside
/// any polygon flagged as a hazard.
///
/// <para>Algorithm: Crossing-number / ray-casting (Sunday 2001). A
/// horizontal ray from the test point eastward counts how many polygon
/// edges it crosses; an odd count means inside. The ray-casting
/// formulation is standard and handles concave polygons cleanly. Edge
/// cases:</para>
/// <list type="bullet">
///   <item>Polygons with fewer than 3 vertices return <c>false</c> -
///   no enclosed area to be inside of.</item>
///   <item>The closing-vertex repetition that GeoJSON requires
///   (<c>ring[n] == ring[0]</c>) is harmless; the algorithm walks
///   adjacent pairs and a degenerate edge contributes zero crossings.</item>
///   <item>Points exactly ON an edge are treated as inside (the
///   <c>&gt;=</c> on the y-bound and the strict <c>&lt;</c> on the x
///   intersect). Documented for callers that care about the
///   distinction; for a hazard alarm "on the edge" should fire, not
///   be a one-pixel safe zone.</item>
/// </list>
///
/// <para>Coordinate convention: the helper is unit-agnostic. SignalK
/// regions arrive as <c>[lat, lon]</c> ring vertices (Leaflet-order
/// after <c>RegionApi.ParseRing</c>), so callers pass <c>lat</c> as
/// the y-axis and <c>lon</c> as the x-axis. The helper just operates
/// on doubles - the lat/lon distortion at the poles is irrelevant
/// for the small-region scales (a few km at most) that anchor /
/// hazard polygons cover.</para>
///
/// <para>Performance: O(n) per polygon; constant work per edge. The
/// caller (HazardousRegionAlarmRule) iterates over all regions per
/// alarm tick (1 Hz), so the loop is on the cold path - no
/// allocations, no LINQ.</para>
/// </summary>
public static class PointInPolygon
{
    /// <summary>True when <paramref name="testY"/>/<paramref name="testX"/>
    /// is inside the closed polygon described by <paramref name="ring"/>.
    /// Each ring vertex is a 2-element array in <c>[y, x]</c> order
    /// (i.e. <c>[lat, lon]</c> for region rings emitted by
    /// <see cref="OnaPlotter.Services.Api.RegionApi.ExtractOuterRings"/>).
    /// Returns false for null / sub-3-vertex rings.</summary>
    public static bool Contains(double[][]? ring, double testY, double testX)
    {
        if (ring is null || ring.Length < 3) return false;

        // Crossing-number algorithm. Walk adjacent edge pairs (i, j)
        // where j is the previous vertex index; count how many
        // edges the eastward horizontal ray from (testY, testX)
        // crosses. Odd count = inside.
        bool inside = false;
        int n = ring.Length;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            // Defensive: an undersized vertex shouldn't crash the
            // alarm loop, just contribute zero crossings.
            if (ring[i] is not { Length: >= 2 } a) continue;
            if (ring[j] is not { Length: >= 2 } b) continue;

            double yi = a[0], xi = a[1];
            double yj = b[0], xj = b[1];

            // Edge straddles the ray's y-line if one endpoint is
            // above and the other strictly below. Strict-on-one-side
            // avoids double-counting at exact-vertex hits.
            bool straddles = (yi > testY) != (yj > testY);
            if (!straddles) continue;

            // X-coordinate where the edge crosses the horizontal
            // ray's y. If that crossing is to the EAST of testX,
            // the ray hits the edge.
            double slope = (xj - xi) * (testY - yi) / (yj - yi);
            double xCross = xi + slope;
            if (testX < xCross) inside = !inside;
        }
        return inside;
    }
}
