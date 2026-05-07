using System.Text.Json;
using OnaPlotter.Models;
using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the exact wire shape ResourceExporter emits per resource
/// type / format pair. These are interop contracts: a downstream
/// tool (OpenCPN, Freeboard, Garmin) that successfully imports
/// today must keep importing tomorrow. A future tweak that would
/// drop a tag or rename a property breaks the shape and these
/// tests fail loud.
/// </summary>
public class ResourceExporterTests
{
    // ---------------- Route -----------------------------------------

    [Test]
    public async Task RouteGpx_EmitsRteWithRteptsInLatLonOrder()
    {
        var route = MakeRoute("Passage A", "Two-leg test",
            // GeoJSON [lon, lat]
            [[8.5, 47.4], [8.6, 47.5], [8.7, 47.6]]);

        var gpx = ResourceExporter.RouteGpx(route);
        await Assert.That(gpx).IsNotNull();
        await Assert.That(gpx!).Contains("<rte");
        await Assert.That(gpx).Contains("<name>Passage A</name>");
        await Assert.That(gpx).Contains("<desc>Two-leg test</desc>");
        // Lat/lon are GPX attribute names; values must be the
        // [lat, lon] swap of the GeoJSON [lon, lat] order.
        await Assert.That(gpx).Contains("lat=\"47.400000\"");
        await Assert.That(gpx).Contains("lon=\"8.500000\"");
        await Assert.That(gpx).Contains("lat=\"47.600000\"");
    }

    [Test]
    public async Task RouteGeoJson_EmitsLineStringInLonLatOrder()
    {
        var route = MakeRoute("Passage B", null, [[8.5, 47.4], [8.6, 47.5]]);
        route.Distance = 12345.6;

        var json = ResourceExporter.RouteGeoJson(route);
        await Assert.That(json).IsNotNull();

        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;
        await Assert.That(root.GetProperty("type").GetString()).IsEqualTo("Feature");
        await Assert.That(root.GetProperty("geometry").GetProperty("type").GetString())
            .IsEqualTo("LineString");

        var coords = root.GetProperty("geometry").GetProperty("coordinates");
        await Assert.That(coords.GetArrayLength()).IsEqualTo(2);
        await Assert.That(coords[0][0].GetDouble()).IsEqualTo(8.5);   // lon first
        await Assert.That(coords[0][1].GetDouble()).IsEqualTo(47.4);  // lat second

        // Distance survives so a re-import doesn't recompute.
        await Assert.That(root.GetProperty("properties").GetProperty("distance").GetDouble())
            .IsEqualTo(12345.6);
    }

    [Test]
    public async Task RouteGpx_NullWhenGeometryMissing()
    {
        var route = new SignalkRoute { Name = "Empty" };
        await Assert.That(ResourceExporter.RouteGpx(route)).IsNull();
        await Assert.That(ResourceExporter.RouteGeoJson(route)).IsNull();
    }

    // ---------------- Waypoint --------------------------------------

    [Test]
    public async Task WaypointGpx_EmitsWptWithName()
    {
        var w = new SignalkWaypoint
        {
            Name = "Marker Buoy",
            Latitude = 47.4,
            Longitude = 8.5,
        };
        var gpx = ResourceExporter.WaypointGpx(w);
        await Assert.That(gpx).IsNotNull();
        await Assert.That(gpx!).Contains("<wpt");
        await Assert.That(gpx).Contains("lat=\"47.400000\"");
        await Assert.That(gpx).Contains("lon=\"8.500000\"");
        await Assert.That(gpx).Contains("<name>Marker Buoy</name>");
    }

    [Test]
    public async Task WaypointGeoJson_EmitsPointInLonLatOrder()
    {
        var w = new SignalkWaypoint
        {
            Name = "Marker",
            Latitude = 47.4,
            Longitude = 8.5,
        };
        var json = ResourceExporter.WaypointGeoJson(w);
        using var doc = JsonDocument.Parse(json!);
        var coords = doc.RootElement.GetProperty("geometry").GetProperty("coordinates");
        await Assert.That(coords[0].GetDouble()).IsEqualTo(8.5);   // lon
        await Assert.That(coords[1].GetDouble()).IsEqualTo(47.4);  // lat
    }

    [Test]
    public async Task WaypointGpx_NullWhenLatLonMissing()
    {
        var w = new SignalkWaypoint { Name = "No-pos" };
        await Assert.That(ResourceExporter.WaypointGpx(w)).IsNull();
        await Assert.That(ResourceExporter.WaypointGeoJson(w)).IsNull();
    }

    // ---------------- Note ------------------------------------------

    [Test]
    public async Task NoteGpx_RoundTripsTitleAndDescription()
    {
        // Notes have no native GPX shape so they degrade to <wpt>
        // with title in <name> and description in <desc>. OpenCPN
        // and Garmin tools preserve both fields on round-trip;
        // pinning the tag mapping here so a future "use <cmt>
        // instead of <desc>" PR doesn't break that quietly.
        var n = new SignalkNote
        {
            Title = "Anchorage Notes",
            Description = "Good holding in 5m mud, watch the rocks N",
            Position = new NotePosition { Latitude = 47.4, Longitude = 8.5 },
        };
        var gpx = ResourceExporter.NoteGpx(n);
        await Assert.That(gpx!).Contains("<name>Anchorage Notes</name>");
        await Assert.That(gpx).Contains("<desc>Good holding in 5m mud, watch the rocks N</desc>");
    }

    [Test]
    public async Task NoteGeoJson_EmitsTitleNotName()
    {
        // SignalK Note uses "title" not "name". Keep the field name
        // on export so a Note re-imported via a SignalK-aware tool
        // lands as a Note, not a Waypoint.
        var n = new SignalkNote
        {
            Title = "Anchorage",
            Description = "Notes here",
            Position = new NotePosition { Latitude = 47.4, Longitude = 8.5 },
        };
        var json = ResourceExporter.NoteGeoJson(n)!;
        using var doc = JsonDocument.Parse(json);
        var props = doc.RootElement.GetProperty("properties");
        await Assert.That(props.GetProperty("title").GetString()).IsEqualTo("Anchorage");
        await Assert.That(props.GetProperty("description").GetString()).IsEqualTo("Notes here");
    }

    // ---------------- Region ----------------------------------------

    [Test]
    public async Task RegionGeoJson_SinglePolygon_ClosesRing()
    {
        // OuterRings are stored Leaflet-order [lat, lon]. The export
        // must swap to [lon, lat] AND close the ring (first = last)
        // per RFC 7946 - some downstream tools (mapbox-gl, QGIS)
        // refuse the polygon if the ring isn't closed.
        var region = new SignalkRegion
        {
            Name = "Restricted Anchorage",
            Description = "No anchoring",
            OuterRings =
            [
                [
                    [47.4, 8.5],
                    [47.4, 8.6],
                    [47.5, 8.6],
                    [47.5, 8.5],
                    // Intentionally NOT closed; exporter must close.
                ],
            ],
        };

        var json = ResourceExporter.RegionGeoJson(region)!;
        using var doc = JsonDocument.Parse(json);
        var geom = doc.RootElement.GetProperty("geometry");
        await Assert.That(geom.GetProperty("type").GetString()).IsEqualTo("Polygon");
        var ring = geom.GetProperty("coordinates")[0];
        await Assert.That(ring.GetArrayLength()).IsEqualTo(5);  // 4 + closure
        // First vs last: same point.
        await Assert.That(ring[0][0].GetDouble()).IsEqualTo(ring[4][0].GetDouble());
        await Assert.That(ring[0][1].GetDouble()).IsEqualTo(ring[4][1].GetDouble());
        // [lon, lat] order in the swap.
        await Assert.That(ring[0][0].GetDouble()).IsEqualTo(8.5);
        await Assert.That(ring[0][1].GetDouble()).IsEqualTo(47.4);
    }

    [Test]
    public async Task RegionGeoJson_MultipleRings_BecomesMultiPolygon()
    {
        var region = new SignalkRegion
        {
            Name = "Two-island restricted area",
            OuterRings =
            [
                [[47.4, 8.5], [47.4, 8.6], [47.5, 8.5], [47.4, 8.5]],
                [[48.0, 9.0], [48.0, 9.1], [48.1, 9.0], [48.0, 9.0]],
            ],
        };
        var json = ResourceExporter.RegionGeoJson(region)!;
        using var doc = JsonDocument.Parse(json);
        await Assert.That(doc.RootElement.GetProperty("geometry").GetProperty("type").GetString())
            .IsEqualTo("MultiPolygon");
    }

    [Test]
    public async Task RegionGeoJson_NullWhenNoRings()
    {
        var region = new SignalkRegion { Name = "Empty" };
        await Assert.That(ResourceExporter.RegionGeoJson(region)).IsNull();
    }

    // ---------------- TRIP (history segment) -------------------------

    [Test]
    public async Task TripGpx_EmitsTrkSegmentWithTimestampedPoints()
    {
        // Two-point synthetic trip. GPX must wrap as <trk><trkseg>
        // with <trkpt><time> per fix; consumers re-importing this
        // (OpenCPN, Garmin BaseCamp) treat it as a track, not a route.
        var t0 = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);
        var pts = new[]
        {
            new TrackPoint(t0,                47.4, 8.5,  null, null, null, null, null, null, null),
            new TrackPoint(t0.AddMinutes(1),  47.5, 8.6,  null, null, null, null, null, null, null),
        };

        var gpx = ResourceExporter.TripGpx("Test trip", pts);

        await Assert.That(gpx).IsNotNull();
        await Assert.That(gpx).Contains("<trk");
        await Assert.That(gpx).Contains("<trkseg");
        await Assert.That(gpx).Contains("<trkpt lat=\"47.400000\" lon=\"8.500000\"");
        await Assert.That(gpx).Contains("<trkpt lat=\"47.500000\" lon=\"8.600000\"");
        await Assert.That(gpx).Contains("<name>Test trip</name>");
        // 'o' format suffix for UTC kind ends with Z.
        await Assert.That(gpx).Contains("Z</time>");
    }

    [Test]
    public async Task TripGpx_NullWhenFewerThanTwoPoints()
    {
        // Single fix isn't a track. Pin: caller-side UI shouldn't
        // offer Export on these, but the helper guards defensively
        // so a stale list-cache state can't ship a one-point GPX.
        var t0 = DateTime.UtcNow;
        var single = new[] {
            new TrackPoint(t0, 47.0, 8.0, null, null, null, null, null, null, null)
        };
        await Assert.That(ResourceExporter.TripGpx("Solo", single)).IsNull();
        await Assert.That(ResourceExporter.TripGpx("Solo", [])).IsNull();
    }

    [Test]
    public async Task TripGeoJson_BuildsLineStringWithStatsProperties()
    {
        var t0 = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);
        var pts = new[]
        {
            new TrackPoint(t0,                47.4, 8.5,  3.0, null, null, null, null, null, null),
            new TrackPoint(t0.AddMinutes(10), 47.5, 8.6,  3.5, null, null, null, null, null, null),
        };
        var seg = new TrackSegment(
            StartUtc: t0,
            EndUtc: t0.AddMinutes(10),
            StartLat: 47.4, StartLon: 8.5,
            EndLat: 47.5, EndLon: 8.6,
            DistanceMetres: 1234.0,
            SogAvgMs: 3.25, SogMaxMs: 3.5, SogMinMs: 3.0,
            WindSpeedAvgMs: 5.5,
            PointCount: 2,
            IsStationary: false);

        var json = ResourceExporter.TripGeoJson("Trip A", seg, pts);

        await Assert.That(json).IsNotNull();
        await Assert.That(json).Contains("\"type\": \"Feature\"");
        await Assert.That(json).Contains("\"type\": \"LineString\"");
        // Coordinates are [lon, lat] per RFC 7946.
        await Assert.That(json).Contains("8.5");
        await Assert.That(json).Contains("47.4");
        await Assert.That(json).Contains("\"name\": \"Trip A\"");
        await Assert.That(json).Contains("\"distanceMeters\": 1234");
        await Assert.That(json).Contains("\"sogAvgMs\": 3.25");
        await Assert.That(json).Contains("\"sogMaxMs\": 3.5");
        await Assert.That(json).Contains("\"twsAvgMs\": 5.5");
        await Assert.That(json).Contains("\"isStationary\": false");
        await Assert.That(json).Contains("\"pointCount\": 2");
    }

    [Test]
    public async Task TripGeoJson_NullWhenFewerThanTwoPoints()
    {
        var t0 = DateTime.UtcNow;
        var seg = new TrackSegment(
            StartUtc: t0, EndUtc: t0,
            StartLat: 0, StartLon: 0, EndLat: 0, EndLon: 0,
            DistanceMetres: 0,
            SogAvgMs: null, SogMaxMs: null, SogMinMs: null,
            WindSpeedAvgMs: null, PointCount: 1, IsStationary: true);
        await Assert.That(ResourceExporter.TripGeoJson("X", seg, [])).IsNull();
        await Assert.That(ResourceExporter.TripGeoJson("X", seg, [
            new TrackPoint(t0, 0, 0, null, null, null, null, null, null, null)
        ])).IsNull();
    }

    // ---------------- helpers ---------------------------------------

    private static SignalkRoute MakeRoute(string name, string? description, double[][] coordsLonLat)
    {
        // Build a JsonElement that looks like the SignalK route
        // payload: feature.geometry.coordinates is the lon/lat array.
        var json = JsonSerializer.Serialize(coordsLonLat);
        using var doc = JsonDocument.Parse(json);
        return new SignalkRoute
        {
            Name = name,
            Description = description,
            Feature = new GeoJsonFeature
            {
                Type = "Feature",
                Geometry = new GeoJsonGeometry
                {
                    Type = "LineString",
                    Coordinates = doc.RootElement.Clone(),
                },
            },
        };
    }
}
