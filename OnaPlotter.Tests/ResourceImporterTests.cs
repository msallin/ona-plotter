using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the import side of the GPX / GeoJSON contract pair. Each
/// shape ResourceExporter emits must round-trip through
/// ResourceImporter back into a recognisable typed record. Format
/// detection is content-sniffing, not extension-based, so the same
/// payload imports regardless of whether the helm renamed it.
/// </summary>
public class ResourceImporterTests
{
    // ---------------- Format detection -----------------------------

    [Test]
    public async Task DetectFormat_LeadingAngleBracket_IsGpx()
    {
        await Assert.That(ResourceImporter.DetectFormat("<?xml version=\"1.0\"?><gpx />"))
            .IsEqualTo(ImportFormat.Gpx);
    }

    [Test]
    public async Task DetectFormat_LeadingBrace_IsGeoJson()
    {
        await Assert.That(ResourceImporter.DetectFormat("  {\"type\":\"Feature\"}"))
            .IsEqualTo(ImportFormat.GeoJson);
    }

    [Test]
    public async Task DetectFormat_LeadingBracket_IsGeoJson()
    {
        await Assert.That(ResourceImporter.DetectFormat("[{\"type\":\"Feature\"}]"))
            .IsEqualTo(ImportFormat.GeoJson);
    }

    [Test]
    public async Task DetectFormat_EmptyOrUnknown_IsNull()
    {
        await Assert.That(ResourceImporter.DetectFormat("")).IsNull();
        await Assert.That(ResourceImporter.DetectFormat("   ")).IsNull();
        await Assert.That(ResourceImporter.DetectFormat("hello")).IsNull();
    }

    // ---------------- GPX parsing ----------------------------------

    [Test]
    public async Task ParseGpx_RouteAndWaypoint()
    {
        var gpx = """
        <?xml version="1.0" encoding="utf-8"?>
        <gpx xmlns="http://www.topografix.com/GPX/1/1" version="1.1">
            <wpt lat="47.4" lon="8.5"><name>Buoy A</name><desc>Notes here</desc></wpt>
            <rte>
                <name>Passage</name>
                <desc>Two-leg test</desc>
                <rtept lat="47.4" lon="8.5"/>
                <rtept lat="47.5" lon="8.6"/>
                <rtept lat="47.6" lon="8.7"/>
            </rte>
        </gpx>
        """;
        var result = ResourceImporter.ParseGpx(gpx);

        await Assert.That(result.Routes.Count).IsEqualTo(1);
        await Assert.That(result.Routes[0].Name).IsEqualTo("Passage");
        await Assert.That(result.Routes[0].Description).IsEqualTo("Two-leg test");
        await Assert.That(result.Routes[0].CoordsLatLon.Length).IsEqualTo(3);
        await Assert.That(result.Routes[0].CoordsLatLon[0][0]).IsEqualTo(47.4);
        await Assert.That(result.Routes[0].CoordsLatLon[0][1]).IsEqualTo(8.5);

        await Assert.That(result.Waypoints.Count).IsEqualTo(1);
        await Assert.That(result.Waypoints[0].Name).IsEqualTo("Buoy A");
        await Assert.That(result.Waypoints[0].Description).IsEqualTo("Notes here");
        await Assert.That(result.Waypoints[0].Lat).IsEqualTo(47.4);

        // GPX has no native note / region shape -> empty.
        await Assert.That(result.Notes.Count).IsEqualTo(0);
        await Assert.That(result.Regions.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ParseGpx_RouteWithLessThanTwoPoints_IsSkipped()
    {
        // GPX writers occasionally emit empty <rte> stubs. Drop
        // them silently rather than fail the import for the
        // legitimate routes alongside.
        var gpx = """
        <?xml version="1.0"?>
        <gpx xmlns="http://www.topografix.com/GPX/1/1" version="1.1">
            <rte><name>Empty</name></rte>
            <rte><name>SinglePoint</name><rtept lat="47" lon="8"/></rte>
            <rte><name>Valid</name>
                <rtept lat="47" lon="8"/>
                <rtept lat="48" lon="9"/>
            </rte>
        </gpx>
        """;
        var result = ResourceImporter.ParseGpx(gpx);
        await Assert.That(result.Routes.Count).IsEqualTo(1);
        await Assert.That(result.Routes[0].Name).IsEqualTo("Valid");
    }

    // ---------------- GeoJSON parsing ------------------------------

    [Test]
    public async Task ParseGeoJson_LineString_ImportsAsRoute()
    {
        var json = """
        { "type": "Feature",
          "properties": { "name": "Passage", "description": "two legs" },
          "geometry": { "type": "LineString",
                        "coordinates": [[8.5, 47.4], [8.6, 47.5]] } }
        """;
        var r = ResourceImporter.ParseGeoJson(json);

        await Assert.That(r.Routes.Count).IsEqualTo(1);
        await Assert.That(r.Routes[0].Name).IsEqualTo("Passage");
        await Assert.That(r.Routes[0].Description).IsEqualTo("two legs");
        // GeoJSON [lon, lat] -> Leaflet [lat, lon].
        await Assert.That(r.Routes[0].CoordsLatLon[0][0]).IsEqualTo(47.4);
        await Assert.That(r.Routes[0].CoordsLatLon[0][1]).IsEqualTo(8.5);
    }

    [Test]
    public async Task ParseGeoJson_PointWithTitle_ImportsAsNote()
    {
        // ResourceExporter emits Notes with a `title` field (NOT
        // `name`). Round-trip: a Point with title comes back as a
        // Note, not a Waypoint.
        var json = """
        { "type": "Feature",
          "properties": { "title": "Anchorage notes",
                          "description": "Good holding" },
          "geometry": { "type": "Point", "coordinates": [8.5, 47.4] } }
        """;
        var r = ResourceImporter.ParseGeoJson(json);

        await Assert.That(r.Notes.Count).IsEqualTo(1);
        await Assert.That(r.Waypoints.Count).IsEqualTo(0);
        await Assert.That(r.Notes[0].Title).IsEqualTo("Anchorage notes");
        await Assert.That(r.Notes[0].Description).IsEqualTo("Good holding");
        await Assert.That(r.Notes[0].Lat).IsEqualTo(47.4);
        await Assert.That(r.Notes[0].Lon).IsEqualTo(8.5);
    }

    [Test]
    public async Task ParseGeoJson_PointWithName_ImportsAsWaypoint()
    {
        var json = """
        { "type": "Feature",
          "properties": { "name": "Marker A" },
          "geometry": { "type": "Point", "coordinates": [8.5, 47.4] } }
        """;
        var r = ResourceImporter.ParseGeoJson(json);

        await Assert.That(r.Waypoints.Count).IsEqualTo(1);
        await Assert.That(r.Notes.Count).IsEqualTo(0);
        await Assert.That(r.Waypoints[0].Name).IsEqualTo("Marker A");
        await Assert.That(r.Waypoints[0].Lat).IsEqualTo(47.4);
    }

    [Test]
    public async Task ParseGeoJson_Polygon_ImportsAsRegion()
    {
        var json = """
        { "type": "Feature",
          "properties": { "name": "Restricted" },
          "geometry": { "type": "Polygon", "coordinates":
            [[[8.5,47.4],[8.6,47.4],[8.6,47.5],[8.5,47.5],[8.5,47.4]]]
          } }
        """;
        var r = ResourceImporter.ParseGeoJson(json);

        await Assert.That(r.Regions.Count).IsEqualTo(1);
        await Assert.That(r.Regions[0].Name).IsEqualTo("Restricted");
        // [lon, lat] -> [lat, lon]
        await Assert.That(r.Regions[0].RingLatLon[0][0]).IsEqualTo(47.4);
        await Assert.That(r.Regions[0].RingLatLon[0][1]).IsEqualTo(8.5);
    }

    [Test]
    public async Task ParseGeoJson_FeatureCollection_DispatchesAllFeatures()
    {
        var json = """
        { "type": "FeatureCollection", "features": [
          { "type": "Feature",
            "properties": { "name": "Passage" },
            "geometry": { "type": "LineString",
                          "coordinates": [[8.5,47.4],[8.6,47.5]] } },
          { "type": "Feature",
            "properties": { "name": "Marker" },
            "geometry": { "type": "Point", "coordinates": [8.5,47.4] } },
          { "type": "Feature",
            "properties": { "title": "Notes" },
            "geometry": { "type": "Point", "coordinates": [8.6,47.5] } }
        ]}
        """;
        var r = ResourceImporter.ParseGeoJson(json);

        await Assert.That(r.Routes.Count).IsEqualTo(1);
        await Assert.That(r.Waypoints.Count).IsEqualTo(1);
        await Assert.That(r.Notes.Count).IsEqualTo(1);
        await Assert.That(r.TotalCount).IsEqualTo(3);
    }

    [Test]
    public async Task ParseGeoJson_MultiPolygon_SuffixesShards()
    {
        // MultiPolygon with two outer rings -> two regions, named
        // "Group" and "Group 2" so the helm sees them as distinct
        // rows on the Regions tab.
        var json = """
        { "type": "Feature",
          "properties": { "name": "Group" },
          "geometry": { "type": "MultiPolygon", "coordinates": [
            [[[8.5,47.4],[8.6,47.4],[8.6,47.5],[8.5,47.4]]],
            [[[9.0,48.0],[9.1,48.0],[9.1,48.1],[9.0,48.0]]]
          ]}}
        """;
        var r = ResourceImporter.ParseGeoJson(json);

        await Assert.That(r.Regions.Count).IsEqualTo(2);
        await Assert.That(r.Regions[0].Name).IsEqualTo("Group");
        await Assert.That(r.Regions[1].Name).IsEqualTo("Group 2");
    }

    // ---------------- Auto-detect Parse ---------------------------

    [Test]
    public async Task Parse_AutoDetectsGpx()
    {
        var content = """
        <?xml version="1.0"?>
        <gpx xmlns="http://www.topografix.com/GPX/1/1" version="1.1">
            <wpt lat="47.4" lon="8.5"><name>Mark</name></wpt>
        </gpx>
        """;
        var r = ResourceImporter.Parse(content);
        await Assert.That(r.Waypoints.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Parse_AutoDetectsGeoJson()
    {
        var content = """
        { "type": "Feature",
          "properties": { "name": "Mark" },
          "geometry": { "type": "Point", "coordinates": [8.5, 47.4] } }
        """;
        var r = ResourceImporter.Parse(content);
        await Assert.That(r.Waypoints.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Parse_UnknownFormat_Throws()
    {
        await Assert.That(() => ResourceImporter.Parse("not a file"))
            .Throws<FormatException>();
    }
}
