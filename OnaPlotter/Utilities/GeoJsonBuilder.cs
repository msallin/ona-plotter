namespace OnaPlotter.Utilities;

/// <summary>
/// Shared GeoJSON body builders for the Signal K <c>/resources/*</c>
/// POST + PUT flows. Routes, waypoints, and regions all send a
/// <c>{ name, feature: {...} }</c> envelope per the SK v2 resource
/// schema; the only differences are geometry type and coordinate
/// shape. Notes use a bare <c>position</c> object and skip this
/// helper (see <see cref="Services.Api.NoteApi"/>).
///
/// <para>The anonymous objects returned here are serialised by
/// System.Text.Json's default camelCase-from-PascalCase mapping --
/// property names are fine as-is. Callers pass coordinates in
/// Leaflet order (<c>[lat, lon]</c>); the builder flips to GeoJSON
/// order (<c>[lon, lat]</c>) internally so no caller has to remember
/// the inversion.</para>
/// </summary>
internal static class GeoJsonBuilder
{
    /// <summary>
    /// Standard body for waypoints + freshly-saved routes. Top-level
    /// <c>name</c> plus a <c>feature</c> with geometry and a
    /// <c>properties</c> block that mirrors the name and carries a
    /// description (empty string when not provided, which SK + Freeboard
    /// expect over a missing key).
    /// </summary>
    public static object FeatureBody(string name, object geometry, string? description = null)
    {
        return new
        {
            name,
            feature = new
            {
                type = "Feature",
                geometry,
                properties = new
                {
                    name,
                    description = description ?? "",
                },
            },
        };
    }

    /// <summary>
    /// Variant used for routes: the <c>properties</c> block gets an
    /// extra <c>coordinatesMeta</c> array (one entry per waypoint,
    /// empty-name placeholders so downstream editors have slots to
    /// fill in), and a top-level <c>distance</c> in metres so the
    /// Layers panel + Resources page can show "12.4 nm" without
    /// having to recompute the haversine sum on every render.
    /// Without the field the sk-resource record had no distance, the
    /// SignalkRoute DTO saw <c>null</c>, and the Routes layer showed
    /// "-" while a Freeboard-saved route on the same server
    /// (Freeboard sends distance) showed the correct value.
    /// </summary>
    public static object RouteFeatureBody(string name, object geometry, int waypointCount, double? distanceMeters, string? description = null)
    {
        var coordinatesMeta = new object[waypointCount];
        for (int i = 0; i < waypointCount; i++) coordinatesMeta[i] = new { name = "" };

        return new
        {
            name,
            distance = distanceMeters,
            feature = new
            {
                type = "Feature",
                geometry,
                properties = new
                {
                    name,
                    description = description ?? "",
                    coordinatesMeta,
                },
            },
        };
    }

    /// <summary>
    /// Region variant: carries <c>description</c> at the TOP level
    /// as well as inside <c>properties</c>. Some Freeboard builds
    /// read the top-level copy, some read the inner one -- shipping
    /// both is the compatible choice.
    /// </summary>
    public static object RegionFeatureBody(string name, object geometry, string? description = null)
    {
        var desc = description ?? "";
        return new
        {
            name,
            description = desc,
            feature = new
            {
                type = "Feature",
                geometry,
                properties = new
                {
                    name,
                    description = desc,
                },
            },
        };
    }

    // --- geometry builders ----------------------------------------

    /// <summary>Point geometry. Caller passes Leaflet-order
    /// (lat, lon); we flip to GeoJSON's (lon, lat).</summary>
    public static object Point(double lat, double lon) =>
        new { type = "Point", coordinates = new[] { lon, lat } };

    /// <summary>LineString (route polyline). Takes a Leaflet-order
    /// array of [lat, lon] pairs, returns GeoJSON-order wrapped in
    /// a type envelope. Caller is responsible for &gt;= 2 vertices.</summary>
    public static object LineString(double[][] leafletLatLon)
    {
        var geo = new double[leafletLatLon.Length][];
        for (int i = 0; i < leafletLatLon.Length; i++)
        {
            var v = leafletLatLon[i];
            geo[i] = [v[1], v[0]];     // swap to [lon, lat]
        }
        return new { type = "LineString", coordinates = geo };
    }

    /// <summary>Outer-ring-only polygon (no holes). Caller provides
    /// the ring already in GeoJSON [lon, lat] order (that's how
    /// RegionApi.BuildCircleRing emits it, so no flip needed here).
    /// The wrapping <c>coordinates</c> is the GeoJSON
    /// <c>array-of-rings</c> shape.</summary>
    public static object Polygon(double[][] geoJsonRing) =>
        new { type = "Polygon", coordinates = new[] { geoJsonRing } };
}
