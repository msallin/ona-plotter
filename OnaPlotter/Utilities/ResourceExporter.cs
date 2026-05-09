using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using OnaPlotter.Models;
using OnaPlotter.Services.Json;

namespace OnaPlotter.Utilities;

/// <summary>
/// Per-resource export to GPX 1.1 or GeoJSON RFC 7946. Routes /
/// waypoints / notes / regions can be saved out individually so the
/// helm can hand a single passage / waypoint to a colleague over
/// chat without sending the whole vault.
/// <para>
/// Format philosophy:
/// </para>
/// <list type="bullet">
///   <item><description>GPX - universal interop with the broad
///     ecosystem of GPS tools and chart software. <c>rte</c> for
///     routes, <c>wpt</c> for points. Notes degrade to <c>wpt</c>
///     with the description in <c>desc</c>. Regions have no native
///     shape in GPX 1.1, so <see cref="RegionGpx"/> is intentionally
///     absent - the UI hides the GPX option on the regions tab.</description></item>
///   <item><description>GeoJSON - browsers + QGIS-style tools + peer
///     SignalK clients. Lossless for everything we store: routes are
///     LineString, waypoints / notes are Point, regions are Polygon
///     (or MultiPolygon for the rare multi-ring shape).</description></item>
/// </list>
/// <para>
/// Bulk-export of routes + waypoints together still lives in
/// <see cref="OnaPlotter.Services.GpxService"/>. This class is the
/// single-resource sibling. Sharing helpers via duplication rather
/// than a base class because the bulk and single shapes diverge in
/// the file metadata (single = creator / 1 element; bulk = creator
/// + many elements + a top-level "Exported by OnaPlotter at ..."
/// annotation Settings.razor adds at call time).
/// </para>
/// </summary>
public static class ResourceExporter
{
    private static readonly XNamespace Gpx = "http://www.topografix.com/GPX/1/1";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // 6 decimals = ~11 cm at the equator. More than enough for a
    // helm passing a passage to a friend; fewer would lose the
    // anchor circle's centre when round-tripped.
    private const string LatLonFormat = "F6";

    // GeoJSON Feature shapes serialise via OnaGeoJsonContext (source-
    // gen, WriteIndented=true). The earlier shared `PrettyJson` options
    // were redundant with the context's options; deleted.

    // ---------------- ROUTE -----------------------------------------

    /// <summary>GPX 1.1 export of a single route. Returns null when
    /// the route has no LineString geometry (defensive: the UI
    /// shouldn't offer Export on a malformed row, but a stale
    /// list-cache could surface one).</summary>
    public static string? RouteGpx(SignalkRoute route)
    {
        if (route.Feature?.Geometry?.Type is not "LineString") return null;
        var rte = new XElement(Gpx + "rte");
        if (!string.IsNullOrEmpty(route.Name))
            rte.Add(new XElement(Gpx + "name", route.Name));
        if (!string.IsNullOrEmpty(route.Description))
            rte.Add(new XElement(Gpx + "desc", route.Description));

        // Coordinates field is a JsonElement; iterate as [lon, lat]
        // per GeoJSON convention and swap to GPX's lat/lon attrs.
        foreach (var pt in route.Feature.Geometry.Coordinates.EnumerateArray())
        {
            var lonLat = ReadLonLat(pt);
            if (lonLat is null) continue;
            rte.Add(new XElement(Gpx + "rtept",
                new XAttribute("lat", lonLat.Value.lat.ToString(LatLonFormat, Inv)),
                new XAttribute("lon", lonLat.Value.lon.ToString(LatLonFormat, Inv))));
        }

        return WrapGpx(rte);
    }

    /// <summary>GeoJSON Feature export of a single route. Always a
    /// LineString; the SignalK route shape's coordinates field is
    /// already in [lon, lat] order, so we just round-trip the
    /// geometry verbatim and rebuild the properties block to drop
    /// SignalK-specific decoration (coordinatesMeta etc.) that
    /// downstream consumers won't recognise.</summary>
    public static string? RouteGeoJson(SignalkRoute route)
    {
        if (route.Feature?.Geometry?.Type is not "LineString") return null;
        var coords = ReadCoords(route.Feature.Geometry.Coordinates);
        if (coords is null) return null;

        var feature = new GeoJsonRouteFeature(
            "Feature",
            new GeoJsonRouteProperties(route.Name ?? "", route.Description ?? "", route.Distance),
            new GeoJsonLineStringGeometry("LineString", coords));
        return JsonSerializer.Serialize(feature, OnaGeoJsonContext.Default.GeoJsonRouteFeature);
    }

    // ---------------- WAYPOINT --------------------------------------

    public static string? WaypointGpx(SignalkWaypoint w)
    {
        if (w.Latitude is null || w.Longitude is null) return null;
        var wpt = new XElement(Gpx + "wpt",
            new XAttribute("lat", w.Latitude.Value.ToString(LatLonFormat, Inv)),
            new XAttribute("lon", w.Longitude.Value.ToString(LatLonFormat, Inv)));
        if (!string.IsNullOrEmpty(w.Name))
            wpt.Add(new XElement(Gpx + "name", w.Name));
        return WrapGpx(wpt);
    }

    public static string? WaypointGeoJson(SignalkWaypoint w)
    {
        if (w.Latitude is null || w.Longitude is null) return null;
        var feature = new GeoJsonWaypointFeature(
            "Feature",
            new GeoJsonNameProperties(w.Name ?? ""),
            // GeoJSON convention is [lon, lat]. Swap from the
            // helm-friendly [lat, lon] used in the model.
            new GeoJsonPointGeometry("Point", [w.Longitude.Value, w.Latitude.Value]));
        return JsonSerializer.Serialize(feature, OnaGeoJsonContext.Default.GeoJsonWaypointFeature);
    }

    // ---------------- NOTE ------------------------------------------

    /// <summary>GPX export of a note. Notes have no native shape in
    /// GPX so they round-trip as <c>wpt</c> with title -> <c>name</c>
    /// and description -> <c>desc</c>. Common GPX-aware tools preserve
    /// both fields on re-import.</summary>
    public static string? NoteGpx(SignalkNote n)
    {
        if (n.Position is null) return null;
        var wpt = new XElement(Gpx + "wpt",
            new XAttribute("lat", n.Position.Latitude.ToString(LatLonFormat, Inv)),
            new XAttribute("lon", n.Position.Longitude.ToString(LatLonFormat, Inv)));
        if (!string.IsNullOrEmpty(n.Title))
            wpt.Add(new XElement(Gpx + "name", n.Title));
        if (!string.IsNullOrEmpty(n.Description))
            wpt.Add(new XElement(Gpx + "desc", n.Description));
        return WrapGpx(wpt);
    }

    public static string? NoteGeoJson(SignalkNote n)
    {
        if (n.Position is null) return null;
        var feature = new GeoJsonNoteFeature(
            "Feature",
            new GeoJsonTitleDescProperties(n.Title ?? "", n.Description ?? ""),
            new GeoJsonPointGeometry("Point", [n.Position.Longitude, n.Position.Latitude]));
        return JsonSerializer.Serialize(feature, OnaGeoJsonContext.Default.GeoJsonNoteFeature);
    }

    // ---------------- TRIP (history segment) ------------------------

    /// <summary>GPX 1.1 export of a single track segment as a
    /// <c>&lt;trk&gt;</c> with one <c>&lt;trkseg&gt;</c>. Each point
    /// emits its lat / lon plus the captured timestamp inside
    /// <c>&lt;time&gt;</c>; standard GPX-aware tools read this back as
    /// a track and can replay it. Returns null when the slice has
    /// fewer than two points (a single fix isn't a track).
    /// </summary>
    /// <param name="name">Track name. Caller passes "Trip yyyy-MM-dd"
    /// or whatever the helm typed.</param>
    /// <param name="points">Chronological points to write. Caller is
    /// responsible for slicing the larger TrackPoint array down to
    /// the segment's window.</param>
    public static string? TripGpx(string name, IReadOnlyList<TrackPoint> points)
    {
        if (points is null || points.Count < 2) return null;
        var trk = new XElement(Gpx + "trk");
        if (!string.IsNullOrEmpty(name))
            trk.Add(new XElement(Gpx + "name", name));
        var seg = new XElement(Gpx + "trkseg");
        foreach (var p in points)
        {
            var pt = new XElement(Gpx + "trkpt",
                new XAttribute("lat", p.Latitude.ToString(LatLonFormat, Inv)),
                new XAttribute("lon", p.Longitude.ToString(LatLonFormat, Inv)));
            // GPX spec uses ISO 8601 in UTC. TrackPoint.Timestamp is
            // already UTC at ingest (TrackApi adjusts to universal
            // before constructing the record); the 'o' format emits
            // the canonical Z-suffixed form.
            pt.Add(new XElement(Gpx + "time",
                p.Timestamp.ToString("o", Inv)));
            seg.Add(pt);
        }
        trk.Add(seg);
        return WrapGpx(trk);
    }

    /// <summary>GeoJSON Feature export of a single trip: a LineString
    /// of [lon, lat] coordinates with a properties bag carrying the
    /// trip's headline stats (name, start / end UTC, duration, distance,
    /// SOG / TWS aggregates). Stats are taken verbatim from the
    /// supplied <see cref="TrackSegment"/> - callers should pass the
    /// segmenter's output rather than recomputing.</summary>
    public static string? TripGeoJson(
        string name, TrackSegment segment, IReadOnlyList<TrackPoint> points)
    {
        if (points is null || points.Count < 2) return null;
        var coords = new double[points.Count][];
        for (int i = 0; i < points.Count; i++)
            coords[i] = [points[i].Longitude, points[i].Latitude];

        var feature = new GeoJsonTripFeature(
            "Feature",
            new GeoJsonTripProperties(
                Name: name ?? "",
                // ISO-8601 in UTC. Same convention as the GPX <time>
                // elements above so downstream tooling sees one format.
                StartUtc: segment.StartUtc.ToString("o", Inv),
                EndUtc: segment.EndUtc.ToString("o", Inv),
                // Numeric fields stay raw so the consumer can format
                // (knots vs m/s, nm vs km, h:mm vs seconds). Nullable
                // doubles ride through unchanged; they emit as
                // "key": null when absent (matches earlier PrettyJson
                // behaviour, no DefaultIgnoreCondition was set).
                DurationSec: segment.Duration.TotalSeconds,
                DistanceMeters: segment.DistanceMetres,
                SogAvgMs: segment.SogAvgMs,
                SogMaxMs: segment.SogMaxMs,
                SogMinMs: segment.SogMinMs,
                TwsAvgMs: segment.WindSpeedAvgMs,
                PointCount: segment.PointCount,
                IsStationary: segment.IsStationary),
            new GeoJsonLineStringGeometry("LineString", coords));
        return JsonSerializer.Serialize(feature, OnaGeoJsonContext.Default.GeoJsonTripFeature);
    }

    // ---------------- REGION ----------------------------------------

    /// <summary>GeoJSON Polygon / MultiPolygon export. GPX has no
    /// equivalent so this is the only format on offer. Outer rings
    /// only - <see cref="SignalkRegion.OuterRings"/> already drops
    /// inner holes during parse, matching the rendering pipeline.
    /// Order: each ring is closed (first == last) per RFC 7946; the
    /// model carries Leaflet-order [lat, lon] which we swap to
    /// [lon, lat] here.</summary>
    public static string? RegionGeoJson(SignalkRegion r)
    {
        if (r.OuterRings.Count == 0) return null;

        // GeoJSON Polygon = array of rings; first ring is the outer,
        // rest are holes. We have only outer rings, so wrap each in
        // a single-ring array. MultiPolygon when there's more than
        // one outer ring (rare; SignalK regions in the wild are
        // almost always single-Polygon).
        var rings = r.OuterRings
            .Select(ring => CloseRing(ring.Select(p => new[] { p[1], p[0] }).ToArray()))
            .ToArray();

        var properties = new GeoJsonNameDescProperties(r.Name ?? "", r.Description ?? "");
        // Single-Polygon and MultiPolygon are distinct record shapes
        // because source-gen wants a concrete type per call site;
        // dispatch on ring count.
        if (rings.Length == 1)
        {
            var feature = new GeoJsonRegionPolygonFeature(
                "Feature",
                properties,
                new GeoJsonPolygonGeometry("Polygon", [rings[0]]));
            return JsonSerializer.Serialize(feature, OnaGeoJsonContext.Default.GeoJsonRegionPolygonFeature);
        }
        else
        {
            var multi = rings.Select(ring => new[] { ring }).ToArray();
            var feature = new GeoJsonRegionMultiPolygonFeature(
                "Feature",
                properties,
                new GeoJsonMultiPolygonGeometry("MultiPolygon", multi));
            return JsonSerializer.Serialize(feature, OnaGeoJsonContext.Default.GeoJsonRegionMultiPolygonFeature);
        }
    }

    // ---------------- helpers ---------------------------------------

    /// <summary>Wraps a single GPX child element in the gpx 1.1
    /// document envelope. Same shape as
    /// <see cref="Services.GpxService.Export"/> emits; a re-import
    /// through that path round-trips losslessly.</summary>
    private static string WrapGpx(XElement child)
    {
        var gpx = new XElement(Gpx + "gpx",
            new XAttribute("version", "1.1"),
            new XAttribute("creator", "OnaPlotter"),
            new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"),
            child);
        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), gpx);
        return doc.ToString();
    }

    /// <summary>GeoJSON [lon, lat] reader for a single
    /// <c>JsonElement</c> coordinate pair. Returns null on
    /// malformed shape so the caller can skip a single bad point
    /// without dropping the whole feature.</summary>
    private static (double lon, double lat)? ReadLonLat(JsonElement pt)
    {
        if (pt.ValueKind != JsonValueKind.Array) return null;
        int i = 0;
        double lon = 0, lat = 0;
        foreach (var v in pt.EnumerateArray())
        {
            if (!v.TryGetDouble(out var d)) return null;
            if (i == 0) lon = d;
            else if (i == 1) lat = d;
            i++;
        }
        if (i < 2) return null;
        return (lon, lat);
    }

    private static double[][]? ReadCoords(JsonElement coords)
    {
        if (coords.ValueKind != JsonValueKind.Array) return null;
        var list = new List<double[]>();
        foreach (var pt in coords.EnumerateArray())
        {
            var lonLat = ReadLonLat(pt);
            if (lonLat is null) continue;
            list.Add([lonLat.Value.lon, lonLat.Value.lat]);
        }
        return list.Count == 0 ? null : [.. list];
    }

    /// <summary>Ensure RFC 7946 polygon-ring closure: first vertex
    /// equals last. Most stored regions are already closed; cheap
    /// to verify defensively.</summary>
    private static double[][] CloseRing(double[][] ring)
    {
        if (ring.Length < 1) return ring;
        var first = ring[0];
        var last = ring[^1];
        if (first[0] == last[0] && first[1] == last[1]) return ring;
        var closed = new double[ring.Length + 1][];
        Array.Copy(ring, closed, ring.Length);
        closed[^1] = first;
        return closed;
    }

    // Properties-bag construction is inlined at each export site
    // now; the per-shape named records (GeoJsonRouteProperties,
    // GeoJsonNameProperties, GeoJsonTitleDescProperties,
    // GeoJsonNameDescProperties) replaced the earlier private helpers
    // that returned `object` and let the reflection serializer pick
    // the shape. See OnaPlotter/Models/GeoJsonDtos.cs.
}
