using System.Net;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the parsing contract for SignalK's track endpoints. TrackApi
/// tries two surfaces: v1 <c>/self/track?timespan=...</c> first, then
/// falls back to v2 <c>/resources/tracks</c>. Both can return either
/// a LineString or MultiLineString shape; both must decode to a flat
/// list of [lat, lon] points. A pre-fix version only handled
/// MultiLineString, which made continuous 24h tracks silently render
/// empty. The v2 surface was missed entirely until openplotter-live
/// probing surfaced the gap.
/// </summary>
public class TrackApiTests
{
    private static TrackApi Api(string jsonBody)
    {
        var http = ApiTestHelpers.JsonClient(_ => jsonBody);
        return new TrackApi(http, ApiTestHelpers.FixedBaseUrl());
    }

    /// <summary>Helper: v1 always returns 404, v2 returns the given body.
    /// Simulates the common "openplotter has v2 only" install.</summary>
    private static TrackApi V2OnlyApi(string v2Body)
    {
        var http = ApiTestHelpers.MockClient(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("/v1/api/self/track", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(v2Body, System.Text.Encoding.UTF8, "application/json"),
            };
        });
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

    [Test]
    public async Task V2_Fallback_Concatenates_Tracks_In_Window()
    {
        // v1 404s (plugin not installed), v2 returns two tracks inside
        // the default 1d window plus one outside it. Parser should
        // include both in-window tracks, concatenate their points in
        // chronological order, and drop the outside one.
        string recentTs = DateTimeOffset.UtcNow.AddHours(-2).ToString("o");
        string olderTs = DateTimeOffset.UtcNow.AddHours(-6).ToString("o");
        string outOfWindowTs = DateTimeOffset.UtcNow.AddDays(-3).ToString("o");
        // Plain interpolation instead of raw-string so nested JSON
        // braces don't fight the raw delimiter escape counting.
        string body =
            "{" +
                "\"recent\":{" +
                    "\"feature\":{\"type\":\"Feature\",\"geometry\":{" +
                        "\"type\":\"MultiLineString\"," +
                        "\"coordinates\":[[[8.60,47.50],[8.61,47.51]]]}}," +
                    "\"timestamp\":\"" + recentTs + "\"}," +
                "\"older\":{" +
                    "\"feature\":{\"type\":\"Feature\",\"geometry\":{" +
                        "\"type\":\"MultiLineString\"," +
                        "\"coordinates\":[[[8.50,47.40],[8.51,47.41]]]}}," +
                    "\"timestamp\":\"" + olderTs + "\"}," +
                "\"ancient\":{" +
                    "\"feature\":{\"type\":\"Feature\",\"geometry\":{" +
                        "\"type\":\"MultiLineString\"," +
                        "\"coordinates\":[[[7.00,46.00]]]}}," +
                    "\"timestamp\":\"" + outOfWindowTs + "\"}" +
            "}";
        var pts = await V2OnlyApi(body).GetServerTrackAsync("1d");

        await Assert.That(pts).IsNotNull();
        await Assert.That(pts!.Length).IsEqualTo(4);  // 2 + 2, ancient dropped
        // Ordered oldest-first so the playback slider advances
        // chronologically: older (8.50) points come before recent (8.60).
        await Assert.That(pts[0][1]).IsEqualTo(8.50);
        await Assert.That(pts[2][1]).IsEqualTo(8.60);
        // Swap verified: [lat, lon] order for Leaflet.
        await Assert.That(pts[0][0]).IsEqualTo(47.40);
    }

    [Test]
    public async Task V2_Fallback_Empty_Returns_Null()
    {
        // Plugin installed but no tracks in the window -> null so the
        // page can show the "no data" hint instead of an empty map.
        var pts = await V2OnlyApi("{}").GetServerTrackAsync("1d");
        await Assert.That(pts).IsNull();
    }

    [Test]
    public async Task V2_Fallback_Skips_Entries_Without_Timestamp()
    {
        // Older v2 track records that pre-date the timestamp field
        // shouldn't crash the parser; just skip them. The window
        // filter can't place them without a timestamp anyway.
        var body = """
        {
            "orphan": {
                "feature": {"type":"Feature","geometry":{
                    "type":"MultiLineString",
                    "coordinates":[[[8.50, 47.40]]]
                }}
            }
        }
        """;
        var pts = await V2OnlyApi(body).GetServerTrackAsync("7d");
        await Assert.That(pts).IsNull();
    }

    [Test]
    public async Task V1_Success_Does_Not_Fall_Through_To_V2()
    {
        // If v1 returns data, v2 should never be queried. Pin the
        // short-circuit so a future refactor doesn't accidentally
        // merge both surfaces and double-count points on servers
        // that expose both.
        int v1Calls = 0, v2Calls = 0;
        var http = ApiTestHelpers.MockClient(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("/v1/api/self/track", StringComparison.Ordinal))
            {
                v1Calls++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"type":"LineString","coordinates":[[8.5,47.4],[8.6,47.5]]}""",
                        System.Text.Encoding.UTF8, "application/json"),
                };
            }
            v2Calls++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var api = new TrackApi(http, ApiTestHelpers.FixedBaseUrl());

        var pts = await api.GetServerTrackAsync();
        await Assert.That(pts).IsNotNull();
        await Assert.That(pts!.Length).IsEqualTo(2);
        await Assert.That(v1Calls).IsEqualTo(1);
        await Assert.That(v2Calls).IsEqualTo(0);
    }
}
