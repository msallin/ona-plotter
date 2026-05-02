using OnaPlotter.Services;

namespace OnaPlotter.Tests;

public class GpxServiceTests
{
    [Test]
    public async Task Parse_ExtractsRoutes()
    {
        var gpx = """
        <?xml version="1.0" encoding="utf-8"?>
        <gpx xmlns="http://www.topografix.com/GPX/1/1" version="1.1" creator="Test">
            <rte>
                <name>Route A</name>
                <rtept lat="47.4" lon="8.5"/>
                <rtept lat="47.5" lon="8.6"/>
                <rtept lat="47.6" lon="8.7"/>
            </rte>
        </gpx>
        """;
        var data = GpxService.Parse(gpx);

        await Assert.That(data.Routes.Count).IsEqualTo(1);
        await Assert.That(data.Routes[0].Name).IsEqualTo("Route A");
        await Assert.That(data.Routes[0].Coords.Length).IsEqualTo(3);
        await Assert.That(data.Routes[0].Coords[0][0]).IsEqualTo(47.4);
        await Assert.That(data.Routes[0].Coords[0][1]).IsEqualTo(8.5);
    }

    [Test]
    public async Task Parse_ExtractsWaypoints()
    {
        var gpx = """
        <?xml version="1.0" encoding="utf-8"?>
        <gpx xmlns="http://www.topografix.com/GPX/1/1" version="1.1">
            <wpt lat="47.4" lon="8.5"><name>Point A</name></wpt>
            <wpt lat="47.5" lon="8.6"><name>Point B</name></wpt>
        </gpx>
        """;
        var data = GpxService.Parse(gpx);

        await Assert.That(data.Waypoints.Count).IsEqualTo(2);
        await Assert.That(data.Waypoints[0].Name).IsEqualTo("Point A");
        await Assert.That(data.Waypoints[0].Lat).IsEqualTo(47.4);
        await Assert.That(data.Waypoints[0].Lon).IsEqualTo(8.5);
    }

    [Test]
    public async Task Parse_SkipsRoutesWithLessThanTwoPoints()
    {
        var gpx = """
        <?xml version="1.0"?>
        <gpx xmlns="http://www.topografix.com/GPX/1/1" version="1.1">
            <rte><name>Single Point</name><rtept lat="47" lon="8"/></rte>
            <rte><name>Valid</name><rtept lat="47" lon="8"/><rtept lat="48" lon="9"/></rte>
        </gpx>
        """;
        var data = GpxService.Parse(gpx);

        await Assert.That(data.Routes.Count).IsEqualTo(1);
        await Assert.That(data.Routes[0].Name).IsEqualTo("Valid");
    }

    [Test]
    public async Task Parse_UsesInvariantCulture()
    {
        // Locale should not affect decimal parsing ('.' always).
        var gpx = """
        <?xml version="1.0"?>
        <gpx xmlns="http://www.topografix.com/GPX/1/1" version="1.1">
            <wpt lat="47.390933" lon="8.123456"><name>Precise</name></wpt>
        </gpx>
        """;
        var data = GpxService.Parse(gpx);

        await Assert.That(data.Waypoints.Count).IsEqualTo(1);
        await Assert.That(data.Waypoints[0].Lat).IsEqualTo(47.390933);
        await Assert.That(data.Waypoints[0].Lon).IsEqualTo(8.123456);
    }

    [Test]
    public async Task Parse_RoundTrip()
    {
        // Import -> GPX schema -> verify via XML parse; we don't have a round-trip
        // Export method that takes GpxData, so just verify Parse is stable.
        var original = """
        <?xml version="1.0"?>
        <gpx xmlns="http://www.topografix.com/GPX/1/1" version="1.1">
            <wpt lat="47.0" lon="8.0"><name>A</name></wpt>
            <rte><name>R</name><rtept lat="47" lon="8"/><rtept lat="48" lon="9"/></rte>
        </gpx>
        """;
        var first = GpxService.Parse(original);
        await Assert.That(first.Routes.Count).IsEqualTo(1);
        await Assert.That(first.Waypoints.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Parse_ThrowsOnMalformedXml()
    {
        await Assert.That(() => GpxService.Parse("<not valid xml"))
            .Throws<System.Xml.XmlException>();
    }

    [Test]
    public async Task Export_EmptyCollections_ValidGpx()
    {
        var xml = GpxService.Export([], []);
        await Assert.That(xml).Contains("<gpx");
        await Assert.That(xml).Contains("version=\"1.1\"");
    }

    // ---------------------------------------------------------------
    // Error-path coverage. Helm-feedback (E1 from the backlog): the
    // import path needs to fail-soft on the kinds of malformation the
    // public web throws at us. A GPX file from a third-party planner
    // can ship missing attributes, the wrong namespace, or coords
    // that don't parse; the import should silently skip the bad
    // entries rather than crash, so a near-good file still loads its
    // good half.
    // ---------------------------------------------------------------

    [Test]
    public async Task Parse_RouteWithSomeMalformedPoints_KeepsTheGoodOnes()
    {
        // Mixed file: two well-formed rtepts and one with a non-numeric
        // lat. The route survives the bad point because TryReadLatLon
        // silently skips it; coords.Count >= 2 still passes.
        const string xml = """
            <gpx xmlns="http://www.topografix.com/GPX/1/1" version="1.1">
              <rte>
                <name>Mixed</name>
                <rtept lat="48.0" lon="-123.0"/>
                <rtept lat="not-a-number" lon="-123.5"/>
                <rtept lat="48.5" lon="-123.5"/>
              </rte>
            </gpx>
            """;
        var data = GpxService.Parse(xml);
        await Assert.That(data.Routes.Count).IsEqualTo(1);
        // Two valid points kept; the malformed one is silently dropped
        // (same shape used by openplotter exports that occasionally
        // emit empty rtepts in the middle of a route).
        await Assert.That(data.Routes[0].Coords.Length).IsEqualTo(2);
    }

    [Test]
    public async Task Parse_RouteWithSinglePoint_DroppedEntirely()
    {
        // GPX semantics: a route with < 2 points isn't a route, it's a
        // waypoint-with-extra-syntax. Drop rather than promote.
        const string xml = """
            <gpx xmlns="http://www.topografix.com/GPX/1/1" version="1.1">
              <rte>
                <name>OnePoint</name>
                <rtept lat="48.0" lon="-123.0"/>
              </rte>
            </gpx>
            """;
        var data = GpxService.Parse(xml);
        await Assert.That(data.Routes.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Parse_WaypointWithMissingLat_DroppedSilently()
    {
        // Helm shouldn't lose every waypoint just because one is malformed.
        const string xml = """
            <gpx xmlns="http://www.topografix.com/GPX/1/1" version="1.1">
              <wpt lon="-123.0"><name>NoLat</name></wpt>
              <wpt lat="48.5" lon="-123.5"><name>Good</name></wpt>
            </gpx>
            """;
        var data = GpxService.Parse(xml);
        await Assert.That(data.Waypoints.Count).IsEqualTo(1);
        await Assert.That(data.Waypoints[0].Name).IsEqualTo("Good");
    }

    [Test]
    public async Task Parse_WrongNamespace_ReturnsEmpty()
    {
        // GPX 1.0 (different namespace) is not supported by the parser.
        // Pin: returns empty rather than throwing, so the caller can
        // surface a "no routes / waypoints" message instead of a crash.
        const string xml = """
            <gpx xmlns="http://www.topografix.com/GPX/1/0" version="1.0">
              <rte>
                <name>Old</name>
                <rtept lat="48.0" lon="-123.0"/>
                <rtept lat="48.5" lon="-123.5"/>
              </rte>
            </gpx>
            """;
        var data = GpxService.Parse(xml);
        await Assert.That(data.Routes.Count).IsEqualTo(0);
        await Assert.That(data.Waypoints.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Parse_EmptyDocument_ReturnsEmpty()
    {
        // Just <gpx/> with no rtes/wpts is a legit empty file.
        const string xml = """<gpx xmlns="http://www.topografix.com/GPX/1/1" version="1.1"/>""";
        var data = GpxService.Parse(xml);
        await Assert.That(data.Routes.Count).IsEqualTo(0);
        await Assert.That(data.Waypoints.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Parse_LatLonOutOfRange_StillParsed()
    {
        // The parser doesn't enforce -90..90 / -180..180; it trusts the
        // file. Document the looseness so a future maintainer doesn't
        // assume validation that isn't there. If it changes, this
        // test surfaces the new contract.
        const string xml = """
            <gpx xmlns="http://www.topografix.com/GPX/1/1" version="1.1">
              <wpt lat="999" lon="-999"><name>Outlandish</name></wpt>
            </gpx>
            """;
        var data = GpxService.Parse(xml);
        await Assert.That(data.Waypoints.Count).IsEqualTo(1);
        await Assert.That(data.Waypoints[0].Lat).IsEqualTo(999);
    }
}
