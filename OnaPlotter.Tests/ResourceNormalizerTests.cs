using System.Text.Json;
using OnaPlotter.Models;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Tests;

/// <summary>
/// The REST loaders and the WebSocket delta handlers share
/// <see cref="ResourceNormalizer"/>; these pin what it keeps, drops and
/// fills in.
/// </summary>
public class ResourceNormalizerTests
{
    private static GeoJsonFeature Feature(string type, string coordinatesJson) => new()
    {
        Type = "Feature",
        Geometry = new GeoJsonGeometry
        {
            Type = type,
            Coordinates = JsonDocument.Parse(coordinatesJson).RootElement,
        },
    };

    [Test]
    public async Task Route_LineString_KeepsAndStampsId()
    {
        var route = new SignalkRoute { Feature = Feature("LineString", "[[1,2],[3,4]]") };

        var result = ResourceNormalizer.Route("r1", route);

        await Assert.That(result).IsSameReferenceAs(route);
        await Assert.That(result!.Id).IsEqualTo("r1");
    }

    [Test]
    public async Task Route_NonLineStringOrMissingFeature_IsDropped()
    {
        await Assert.That(ResourceNormalizer.Route("r1",
            new SignalkRoute { Feature = Feature("Polygon", "[]") })).IsNull();
        await Assert.That(ResourceNormalizer.Route("r2", new SignalkRoute())).IsNull();
    }

    [Test]
    public async Task Waypoint_PointGeometry_FillsLatLon()
    {
        var wp = new SignalkWaypoint { Feature = Feature("Point", "[-81.1,24.7]") };

        var result = ResourceNormalizer.Waypoint("w1", wp);

        await Assert.That(result.Id).IsEqualTo("w1");
        await Assert.That(result.Latitude).IsEqualTo(24.7);
        await Assert.That(result.Longitude).IsEqualTo(-81.1);
    }

    [Test]
    public async Task Waypoint_MalformedPoint_KeepsWaypointWithoutPosition()
    {
        var wp = new SignalkWaypoint { Feature = Feature("Point", "[null,24.7]") };

        var result = ResourceNormalizer.Waypoint("w1", wp);

        await Assert.That(result.Id).IsEqualTo("w1");
        await Assert.That(result.Latitude).IsNull();
        await Assert.That(result.Longitude).IsNull();
    }

    [Test]
    public async Task Note_WithoutPosition_IsDropped()
    {
        await Assert.That(ResourceNormalizer.Note("n1", new SignalkNote())).IsNull();
    }

    [Test]
    public async Task Note_WithPosition_StampsId()
    {
        var note = new SignalkNote { Position = new NotePosition { Latitude = 1, Longitude = 2 } };

        var result = ResourceNormalizer.Note("n1", note);

        await Assert.That(result!.Id).IsEqualTo("n1");
    }

    [Test]
    public async Task Region_Polygon_FillsOuterRingsInLeafletOrder()
    {
        var region = new SignalkRegion
        {
            Feature = Feature("Polygon", "[[[8.5,47.4],[8.6,47.4],[8.6,47.5],[8.5,47.4]]]"),
        };

        var result = ResourceNormalizer.Region("g1", region);

        await Assert.That(result!.Id).IsEqualTo("g1");
        await Assert.That(result.OuterRings.Count).IsEqualTo(1);
        await Assert.That(result.OuterRings[0][0][0]).IsEqualTo(47.4);
        await Assert.That(result.OuterRings[0][0][1]).IsEqualTo(8.5);
    }

    [Test]
    public async Task Region_WithInvalidVertex_ThrowsInvalidData()
    {
        var region = new SignalkRegion
        {
            Feature = Feature("Polygon", "[[[8.5,47.4],[null,47.4],[8.6,47.5],[8.5,47.4]]]"),
        };

        await Assert.That(() => ResourceNormalizer.Region("g1", region))
            .Throws<InvalidDataException>();
    }

    [Test]
    public async Task Region_WithoutRenderableGeometry_IsDropped()
    {
        await Assert.That(ResourceNormalizer.Region("g1", new SignalkRegion())).IsNull();
        await Assert.That(ResourceNormalizer.Region("g2",
            new SignalkRegion { Feature = Feature("Point", "[1,2]") })).IsNull();
    }
}
