using OnaPlotter.Services.Api;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the parsing contract for SignalK's /self/track endpoint. The
/// endpoint can return either a LineString (continuous track) or a
/// MultiLineString (track with gaps); both shapes must decode to a flat
/// list of [lat, lon] points. A pre-fix version of TrackApi only handled
/// MultiLineString, which made a continuous 24h track silently render
/// empty.
/// </summary>
public class TrackApiTests
{
    private static TrackApi Api(string jsonBody)
    {
        var http = ApiTestHelpers.JsonClient(_ => jsonBody);
        return new TrackApi(http, ApiTestHelpers.FixedBaseUrl());
    }

    [Test]
    public async Task LineString_Shape_Decodes_All_Points()
    {
        // Continuous track: GeoJSON LineString with a flat array of points.
        // [lon, lat, time?] tuples -- Leaflet wants [lat, lon].
        var body = """
        {
            "type": "LineString",
            "coordinates": [
                [8.50, 47.40, "2024-01-01T00:00Z"],
                [8.51, 47.41, "2024-01-01T00:01Z"],
                [8.52, 47.42, "2024-01-01T00:02Z"]
            ]
        }
        """;
        var pts = await Api(body).GetServerTrackAsync();

        await Assert.That(pts).IsNotNull();
        await Assert.That(pts!.Length).IsEqualTo(3);
        // [lat, lon] swap verified on the first point.
        await Assert.That(pts[0][0]).IsEqualTo(47.40);
        await Assert.That(pts[0][1]).IsEqualTo(8.50);
        await Assert.That(pts[2][0]).IsEqualTo(47.42);
        await Assert.That(pts[2][1]).IsEqualTo(8.52);
    }

    [Test]
    public async Task MultiLineString_Shape_Flattens_All_Segments()
    {
        // Gapped track: two line segments. Downstream renderer doesn't care
        // about the gap (polyline will straight-line between them), but the
        // parser must at least not drop points.
        var body = """
        {
            "type": "MultiLineString",
            "coordinates": [
                [[8.50, 47.40], [8.51, 47.41]],
                [[8.60, 47.50], [8.61, 47.51]]
            ]
        }
        """;
        var pts = await Api(body).GetServerTrackAsync();

        await Assert.That(pts).IsNotNull();
        await Assert.That(pts!.Length).IsEqualTo(4);
        await Assert.That(pts[0][0]).IsEqualTo(47.40);
        await Assert.That(pts[3][0]).IsEqualTo(47.51);
    }

    [Test]
    public async Task Empty_Coordinates_Returns_Null()
    {
        var body = """{ "type": "LineString", "coordinates": [] }""";
        var pts = await Api(body).GetServerTrackAsync();
        await Assert.That(pts).IsNull();
    }

    [Test]
    public async Task Missing_Coordinates_Returns_Null()
    {
        var body = """{ "type": "LineString" }""";
        var pts = await Api(body).GetServerTrackAsync();
        await Assert.That(pts).IsNull();
    }

    [Test]
    public async Task Malformed_Point_Is_Skipped()
    {
        // Defensive: a stray scalar or too-short point shouldn't crash the
        // parser, just drop that one point.
        var body = """
        {
            "type": "LineString",
            "coordinates": [
                [8.50, 47.40],
                "garbage",
                [8.52],
                [8.51, 47.41]
            ]
        }
        """;
        var pts = await Api(body).GetServerTrackAsync();
        await Assert.That(pts).IsNotNull();
        await Assert.That(pts!.Length).IsEqualTo(2);
    }
}
