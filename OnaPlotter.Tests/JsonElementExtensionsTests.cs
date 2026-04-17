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
}
