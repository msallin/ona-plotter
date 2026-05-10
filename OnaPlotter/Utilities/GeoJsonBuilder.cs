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
/// System.Text.Json's default camelCase-from-PascalCase mapping -
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
    /// description (empty string when not provided, which SK +
    /// peer SignalK clients expect over a missing key).
    /// <para>Optional <paramref name="createdAt"/> rides as a top-level
    /// custom field (not part of the SK schema, but resources-fs
    /// round-trips arbitrary fields). When null the field is omitted
    /// entirely so existing servers / clients see no change in
    /// shape.</para>
    /// </summary>
    public static object FeatureBody(string name, object geometry, string? description = null, DateTime? createdAt = null,
        bool? isMob = null, bool? isActive = null, string? mobAlarmId = null)
    {
        // MOB metadata rides INSIDE feature.properties only. The
        // post-deserialise lift in WaypointApi.GetAllAsync +
        // ResourceStore.HandleWaypointDelta reads from there; nothing
        // reads a top-level copy. Conditional emit so non-MOB waypoints
        // don't carry empty MOB keys on the wire.
        var feature = (isMob is null && isActive is null && mobAlarmId is null)
            ? (object)new
            {
                type = "Feature",
                geometry,
                properties = new
                {
                    name,
                    description = description ?? "",
                },
            }
            : new
            {
                type = "Feature",
                geometry,
                properties = new
                {
                    name,
                    description = description ?? "",
                    isMob,
                    isActive,
                    mobAlarmId,
                },
            };
        // Two top-level shapes (with-createdAt + without) so JSON
        // output doesn't carry literal "createdAt": null. Anonymous
        // objects keep System.Text.Json output stable without standing
        // up named record types.
        if (createdAt is DateTime t)
        {
            string createdAtIso = t.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
            return new { name, feature, createdAt = createdAtIso };
        }
        return new { name, feature };
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
    /// "-" while a route saved by a peer SignalK client on the same
    /// server (peers send distance) showed the correct value.
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
    /// as well as inside <c>properties</c>. Some SignalK clients
    /// read the top-level copy, some read the inner one - shipping
    /// both is the compatible choice. The <paramref name="isHazard"/>
    /// flag rides along the same way: top level (where the C# DTO
    /// reads it back) and inside properties (so a future
    /// non-OnaPlotter consumer that walks GeoJSON-only sees it too).
    /// <para>The optional <paramref name="createdAt"/> /
    /// <paramref name="centerLat"/> / <paramref name="centerLon"/> /
    /// <paramref name="radiusMeters"/> ride along at the top level
    /// only - the SignalK resources-fs provider preserves them
    /// across round-trip; the GeoJSON `properties` block stays the
    /// peer-facing surface so non-OnaPlotter consumers see the
    /// minimum compatible shape.</para>
    /// </summary>
    public static object RegionFeatureBody(string name, object geometry,
        string? description = null, bool isHazard = false,
        DateTime? createdAt = null,
        double? centerLat = null, double? centerLon = null, double? radiusMeters = null)
    {
        var desc = description ?? "";
        return new
        {
            name,
            description = desc,
            isHazard,
            createdAt,
            centerLat,
            centerLon,
            radiusMeters,
            feature = new
            {
                type = "Feature",
                geometry,
                properties = new
                {
                    name,
                    description = desc,
                    isHazard,
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
    /// CircleGeometry.BuildRing emits it, so no flip needed here).
    /// The wrapping <c>coordinates</c> is the GeoJSON
    /// <c>array-of-rings</c> shape.</summary>
    public static object Polygon(double[][] geoJsonRing) =>
        new { type = "Polygon", coordinates = new[] { geoJsonRing } };
}
