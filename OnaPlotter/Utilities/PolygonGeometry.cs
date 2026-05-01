namespace OnaPlotter.Utilities;

/// <summary>
/// Pure geometry helpers for user-drawn polygons (regions / no-go zones /
/// anchorages). The renderer (JS) emits the vertex array; metrics like
/// area belong here so the formula can be unit-tested without a browser
/// and any future C#-side consumer (CSV export, region overlap check)
/// uses the same number the polygon-edit panel displays.
/// </summary>
public static class PolygonGeometry
{
    /// <summary>
    /// Area of a polygon defined by [lat, lon] vertex pairs, in square
    /// metres. Uses the shoelace formula on an equirectangular projection
    /// anchored at the first vertex's latitude -- accurate to a small
    /// fraction of a percent for the typical 100 m to 10 km regions
    /// sailors draw, and avoids the cost of a proper geodesic area for a
    /// metric the helm reads as "roughly this many hectares".
    /// </summary>
    /// <remarks>
    /// Returns 0 for fewer than three vertices (no enclosed area). The
    /// ring is treated as closed implicitly -- the last vertex links back
    /// to the first; callers do not need to repeat the opening point.
    /// Sign of the signed area is discarded so winding order does not
    /// matter (helms drawing clockwise vs counter-clockwise both get a
    /// positive metric).
    /// </remarks>
    public static double AreaSquareMeters(double[][] coords)
    {
        if (coords is null || coords.Length < 3) return 0;

        // Anchor scale at the first vertex's latitude; sailing-scale
        // regions span well under a degree of latitude, so the cosine
        // is effectively constant across the polygon.
        double lat0Rad = coords[0][0] * Math.PI / 180.0;
        double cosLat = Math.Cos(lat0Rad);
        double metersPerDegLon = CircleGeometry.MetersPerDegLatitude * cosLat;

        double signedDoubleArea = 0;
        int n = coords.Length;
        for (int i = 0; i < n; i++)
        {
            double[] a = coords[i];
            double[] b = coords[(i + 1) % n];
            if (a is null || b is null || a.Length < 2 || b.Length < 2) continue;

            double x1 = a[1] * metersPerDegLon;
            double y1 = a[0] * CircleGeometry.MetersPerDegLatitude;
            double x2 = b[1] * metersPerDegLon;
            double y2 = b[0] * CircleGeometry.MetersPerDegLatitude;
            signedDoubleArea += (x1 * y2) - (x2 * y1);
        }

        return Math.Abs(signedDoubleArea) / 2.0;
    }
}
