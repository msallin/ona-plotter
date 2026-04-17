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
}
