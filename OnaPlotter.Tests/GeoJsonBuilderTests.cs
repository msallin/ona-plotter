using System.Text.Json;
using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

/// <summary>
/// Wire-shape pinning for GeoJsonBuilder. Output lands in HTTP
/// request bodies that Signal K servers (+ Freeboard-SK) parse
/// directly, so a shape change -- a missing "properties" block, a
/// flipped coord order -- silently breaks resource creation. Round-
/// tripping each builder through JsonSerializer lets us assert the
/// exact wire JSON without hand-rolling it.
/// </summary>
public class GeoJsonBuilderTests
{
    private static string Serialize(object body) =>
        JsonSerializer.Serialize(body,
            new JsonSerializerOptions { WriteIndented = false });

    [Test]
    public async Task FeatureBody_EmitsFullFeatureEnvelope_WithEmptyDescriptionByDefault()
    {
        var body = GeoJsonBuilder.FeatureBody(
            "WPT A",
            GeoJsonBuilder.Point(47.4, 8.5));
        var json = Serialize(body);

        // Top-level name + feature envelope.
        await Assert.That(json).Contains("\"name\":\"WPT A\"");
        await Assert.That(json).Contains("\"type\":\"Feature\"");
        // Point geometry, coords in GeoJSON [lon, lat] order.
        await Assert.That(json).Contains("\"type\":\"Point\"");
        await Assert.That(json).Contains("\"coordinates\":[8.5,47.4]");
        // Properties block is required by Freeboard-SK; empty string
        // description (not a missing key) is the spec-friendly default.
        await Assert.That(json).Contains("\"properties\":");
        await Assert.That(json).Contains("\"description\":\"\"");
    }

    [Test]
    public async Task FeatureBody_EmitsDescriptionWhenProvided()
    {
        var body = GeoJsonBuilder.FeatureBody(
            "WPT B", GeoJsonBuilder.Point(0, 0), "waypoint next to anchor");
        var json = Serialize(body);
        await Assert.That(json).Contains("\"description\":\"waypoint next to anchor\"");
    }

    [Test]
    public async Task LineString_FlipsLeafletPairsToGeoJsonOrder()
    {
        // Leaflet hands us [[lat, lon], ...]; GeoJSON wants [[lon, lat], ...].
        var leaflet = new[]
        {
            new[] { 47.4, 8.5 },
            new[] { 47.5, 8.6 },
            new[] { 47.6, 8.7 },
        };
        var geom = GeoJsonBuilder.LineString(leaflet);
        var json = Serialize(geom);

        await Assert.That(json).Contains("\"type\":\"LineString\"");
        // Each pair inverted.
        await Assert.That(json).Contains("[8.5,47.4]");
        await Assert.That(json).Contains("[8.6,47.5]");
        await Assert.That(json).Contains("[8.7,47.6]");
    }

    [Test]
    public async Task RouteFeatureBody_EmitsCoordinatesMeta_OneEntryPerWaypoint()
    {
        var body = GeoJsonBuilder.RouteFeatureBody(
            "My Passage",
            GeoJsonBuilder.LineString(new[] { new[] { 0.0, 0.0 }, new[] { 1.0, 1.0 }, new[] { 2.0, 2.0 } }),
            waypointCount: 3,
            distanceMeters: null);
        var json = Serialize(body);

        await Assert.That(json).Contains("\"coordinatesMeta\":");
        // 3 empty-name slots. Exact count matters because chartplotters
        // index into this by waypoint index.
        var occurrences = json.Split("{\"name\":\"\"}").Length - 1;
        await Assert.That(occurrences).IsEqualTo(3);
    }

    [Test]
    public async Task RouteFeatureBody_EmitsTopLevelDistance_WhenSupplied()
    {
        // Routes saved by OnaPlotter used to drop the top-level
        // distance field, so SignalkRoute.Distance round-tripped as
        // null and the Layers panel + Resources page rendered "-"
        // for any OnaPlotter-saved route. Freeboard always sends
        // distance; this test pins the parity.
        var body = GeoJsonBuilder.RouteFeatureBody(
            "With distance",
            GeoJsonBuilder.LineString(new[] { new[] { 0.0, 0.0 }, new[] { 0.5, 0.5 } }),
            waypointCount: 2,
            distanceMeters: 12345.6);
        var json = Serialize(body);

        await Assert.That(json).Contains("\"distance\":12345.6");
    }

    [Test]
    public async Task RegionFeatureBody_CarriesDescriptionAtBothLevels()
    {
        // Regions are the odd one out -- description at top level AND
        // in properties because different Freeboard builds read
        // different copies. The isHazard flag rides along the same
        // way (top + nested) for the same compatibility reason.
        var ring = new[]
        {
            new[] { 8.5, 47.4 },
            new[] { 8.6, 47.4 },
            new[] { 8.6, 47.5 },
            new[] { 8.5, 47.5 },
            new[] { 8.5, 47.4 },
        };
        var body = GeoJsonBuilder.RegionFeatureBody(
            "Harbour", GeoJsonBuilder.Polygon(ring), "no anchor zone");
        var json = Serialize(body);

        // Top-level description + isHazard (defaults to false).
        await Assert.That(json).StartsWith("{\"name\":\"Harbour\",\"description\":\"no anchor zone\",\"isHazard\":false");
        // And inside properties.
        await Assert.That(json).Contains("\"properties\":{\"name\":\"Harbour\",\"description\":\"no anchor zone\",\"isHazard\":false}");
        // Polygon coords nested one level deeper than LineString
        // (array-of-rings); ring already in GeoJSON order so no flip.
        await Assert.That(json).Contains("\"type\":\"Polygon\"");
        await Assert.That(json).Contains("[[8.5,47.4],[8.6,47.4]");
    }

    [Test]
    public async Task RegionFeatureBody_EmitsIsHazardAtBothLevels()
    {
        // Pin the wire shape for the isHazard flag: top-level (where
        // SignalkRegion.IsHazard reads it back via the [JsonPropertyName]
        // mapping) AND inside properties (so a non-OnaPlotter consumer
        // walking GeoJSON-only sees it). A refactor that drops either
        // copy fails this test instead of silently desyncing one side.
        var ring = new[]
        {
            new[] { 0.0, 0.0 },
            new[] { 0.0, 1.0 },
            new[] { 1.0, 1.0 },
            new[] { 1.0, 0.0 },
            new[] { 0.0, 0.0 },
        };
        var body = GeoJsonBuilder.RegionFeatureBody(
            "Reefs", GeoJsonBuilder.Polygon(ring), description: "rocky", isHazard: true);
        var json = Serialize(body);

        await Assert.That(json).Contains("\"isHazard\":true");
        // Both levels carry it; assert each occurrence is paired with
        // the right scope rather than just counting "true" globally.
        await Assert.That(json).Contains(",\"isHazard\":true,\"feature\":");
        await Assert.That(json).Contains("\"description\":\"rocky\",\"isHazard\":true}");
    }

    [Test]
    public async Task RegionFeatureBody_DefaultIsHazardFalse()
    {
        var ring = new[]
        {
            new[] { 0.0, 0.0 },
            new[] { 0.0, 1.0 },
            new[] { 1.0, 0.0 },
            new[] { 0.0, 0.0 },
        };
        var body = GeoJsonBuilder.RegionFeatureBody(
            "Anchorage", GeoJsonBuilder.Polygon(ring));
        var json = Serialize(body);
        await Assert.That(json).Contains("\"isHazard\":false");
    }

    [Test]
    public async Task Point_NullDescriptionRendersAsEmptyString()
    {
        // A missing-key description silently breaks Freeboard's
        // waypoint rendering; pin that null -> "" instead.
        var body = GeoJsonBuilder.FeatureBody(
            "WPT", GeoJsonBuilder.Point(0, 0), description: null);
        var json = Serialize(body);
        await Assert.That(json).Contains("\"description\":\"\"");
    }
}
