using System.Text.Json;
using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

public class JsonElementExtensionsTests
{
    [Test]
    public async Task FlattenSignalKPaths_FindsLeafValues()
    {
        var json = """
        {
            "navigation": {
                "position": { "value": { "latitude": 47.0, "longitude": 8.0 } },
                "speedOverGround": { "value": 5.5 }
            },
            "environment": {
                "wind": {
                    "speedApparent": { "value": 3.2 }
                }
            }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var paths = doc.RootElement.FlattenSignalKPaths();

        await Assert.That(paths).Contains("environment.wind.speedApparent");
        await Assert.That(paths).Contains("navigation.position");
        await Assert.That(paths).Contains("navigation.speedOverGround");
        await Assert.That(paths.Count).IsEqualTo(3);
    }

    [Test]
    public async Task FlattenSignalKPaths_SortedCaseInsensitive()
    {
        var json = """{"Zebra":{"value":1},"apple":{"value":2}}""";
        using var doc = JsonDocument.Parse(json);
        var paths = doc.RootElement.FlattenSignalKPaths();

        // Case-insensitive sort: "apple" before "Zebra".
        await Assert.That(paths[0]).IsEqualTo("apple");
        await Assert.That(paths[1]).IsEqualTo("Zebra");
    }

    [Test]
    public async Task FlattenSignalKPaths_SkipsNodesWithoutValue()
    {
        // Parent nodes with 'value' are leaves; their children are not explored.
        var json = """
        {
            "a": { "value": 1, "nested": { "value": 2 } },
            "b": { "nested": { "value": 3 } }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var paths = doc.RootElement.FlattenSignalKPaths();

        await Assert.That(paths).Contains("a");
        await Assert.That(paths).Contains("b.nested");
        await Assert.That(paths.Count).IsEqualTo(2);
    }

    [Test]
    public async Task ToLeafletLineString_ConvertsLonLatToLatLon()
    {
        // GeoJSON [[lon, lat], ...] -> Leaflet [[lat, lon], ...]
        var json = "[[8.5,47.4],[9.0,48.0]]";
        using var doc = JsonDocument.Parse(json);
        var coords = doc.RootElement.ToLeafletLineString();

        await Assert.That(coords.Length).IsEqualTo(2);
        await Assert.That(coords[0][0]).IsEqualTo(47.4);
        await Assert.That(coords[0][1]).IsEqualTo(8.5);
        await Assert.That(coords[1][0]).IsEqualTo(48.0);
        await Assert.That(coords[1][1]).IsEqualTo(9.0);
    }

    [Test]
    public async Task ToLeafletLineString_EmptyForMalformed()
    {
        using var doc = JsonDocument.Parse("{}");
        var coords = doc.RootElement.ToLeafletLineString();
        await Assert.That(coords.Length).IsEqualTo(0);
    }

    [Test]
    public async Task ToLatLonPoint_ParsesPoint()
    {
        using var doc = JsonDocument.Parse("[8.5, 47.4]");
        var result = doc.RootElement.ToLatLonPoint();

        await Assert.That(result.HasValue).IsTrue();
        await Assert.That(result!.Value.Latitude).IsEqualTo(47.4);
        await Assert.That(result.Value.Longitude).IsEqualTo(8.5);
    }

    [Test]
    public async Task ToLatLonPoint_NullForObject()
    {
        using var doc = JsonDocument.Parse("{}");
        var result = doc.RootElement.ToLatLonPoint();
        await Assert.That(result.HasValue).IsFalse();
    }

    [Test]
    public async Task LineStringBounds_ComputesMinMax()
    {
        var json = "[[8.0,47.0],[9.5,48.5],[7.5,47.2]]";
        using var doc = JsonDocument.Parse(json);
        var bounds = doc.RootElement.LineStringBounds();

        await Assert.That(bounds.HasValue).IsTrue();
        await Assert.That(bounds!.Value.MinLat).IsEqualTo(47.0);
        await Assert.That(bounds.Value.MaxLat).IsEqualTo(48.5);
        await Assert.That(bounds.Value.MinLon).IsEqualTo(7.5);
        await Assert.That(bounds.Value.MaxLon).IsEqualTo(9.5);
    }

    [Test]
    public async Task LineStringBounds_NullForEmpty()
    {
        using var doc = JsonDocument.Parse("[]");
        var bounds = doc.RootElement.LineStringBounds();
        await Assert.That(bounds.HasValue).IsFalse();
    }

    // --- TryGetLatLon ------------------------------------------------

    private static (bool Ok, double Lat, double Lon) LatLon(string json)
    {
        using var doc = JsonDocument.Parse(json);
        bool ok = doc.RootElement.TryGetLatLon(out double lat, out double lon);
        return (ok, lat, lon);
    }

    [Test]
    public async Task TryGetLatLon_BareForm()
    {
        // navigation.position as signalk-server publishes it.
        var r = LatLon("""{"latitude": 59.91, "longitude": 10.75, "altitude": 2.1}""");
        await Assert.That(r).IsEqualTo((true, 59.91, 10.75));
    }

    [Test]
    public async Task TryGetLatLon_NestedPositionForm()
    {
        // navigation.course.nextPoint parent object (v2 course API).
        var r = LatLon("""{"type": "RoutePoint", "href": null, "position": {"latitude": -33.86, "longitude": 151.21}}""");
        await Assert.That(r).IsEqualTo((true, -33.86, 151.21));
    }

    [Test]
    public async Task TryGetLatLon_BareWinsOverNested()
    {
        var r = LatLon("""{"latitude": 1, "longitude": 2, "position": {"latitude": 3, "longitude": 4}}""");
        await Assert.That(r).IsEqualTo((true, 1.0, 2.0));
    }

    [Test]
    public async Task TryGetLatLon_FallsBackToNested_WhenBareIncomplete()
    {
        var r = LatLon("""{"latitude": 1, "position": {"latitude": 3, "longitude": 4}}""");
        await Assert.That(r).IsEqualTo((true, 3.0, 4.0));
    }

    [Test]
    [Arguments("""{"latitude": 0, "longitude": 0}""", 0.0, 0.0)]
    [Arguments("""{"latitude": 90, "longitude": 180}""", 90.0, 180.0)]
    [Arguments("""{"latitude": -90, "longitude": -180}""", -90.0, -180.0)]
    [Arguments("""{"latitude": 47, "longitude": 8}""", 47.0, 8.0)]
    [Arguments("""{"latitude": 4.7e1, "longitude": 8e0}""", 47.0, 8.0)]
    public async Task TryGetLatLon_NumericBoundaries(string json, double lat, double lon)
    {
        await Assert.That(LatLon(json)).IsEqualTo((true, lat, lon));
    }

    [Test]
    [Arguments("""{"latitude": 47.0}""")]
    [Arguments("""{"longitude": 8.0}""")]
    [Arguments("""{"latitude": "47.0", "longitude": 8.0}""")]
    [Arguments("""{"latitude": 47.0, "longitude": null}""")]
    [Arguments("""{"latitude": 1e400, "longitude": 8.0}""")]
    [Arguments("""{}""")]
    [Arguments("""{"position": null}""")]
    [Arguments("""{"position": {"latitude": 47.0}}""")]
    [Arguments("""{"value": {"position": {"latitude": 1, "longitude": 2}}}""")]
    [Arguments("""{"position": {"position": {"latitude": 1, "longitude": 2}}}""")]
    [Arguments("""[8.0, 47.0]""")]
    [Arguments("""null""")]
    [Arguments("""47.0""")]
    [Arguments("\"47,8\"")]
    public async Task TryGetLatLon_RejectsMalformed_WithZeroedOutputs(string json)
    {
        // A half-parsed fix must never leak: outputs are zeroed on false.
        await Assert.That(LatLon(json)).IsEqualTo((false, 0.0, 0.0));
    }
}
