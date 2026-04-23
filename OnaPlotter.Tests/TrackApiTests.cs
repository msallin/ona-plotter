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

    /// <summary>Helper: v1 + history always return 404, v2 tracks
    /// returns the given body. Simulates the "openplotter has v2
    /// tracks only, no history plugin" install.</summary>
    private static TrackApi V2OnlyApi(string v2Body)
    {
        var http = ApiTestHelpers.MockClient(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("/v1/api/self/track", StringComparison.Ordinal)
                || path.Contains("/history/values", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(v2Body, System.Text.Encoding.UTF8, "application/json"),
            };
        });
        return new TrackApi(http, ApiTestHelpers.FixedBaseUrl());
    }

    /// <summary>Helper: history API returns the given body; others
    /// return 404. Simulates the signalk-parquet install where the
    /// History API v2 is the only surface available.</summary>
    private static TrackApi HistoryOnlyApi(string historyBody,
        Action<HttpRequestMessage>? onRequest = null)
    {
        var http = ApiTestHelpers.MockClient(req =>
        {
            onRequest?.Invoke(req);
            var path = req.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("/history/values", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(historyBody, System.Text.Encoding.UTF8, "application/json"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
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
    public async Task History_API_Array_Value_Shape_Parses()
    {
        // signalk-parquet default: position value is [lon, lat].
        // Pin the parse + axis swap so switching the History page to
        // the v2 History API doesn't silently render points mirrored
        // across the meridian.
        string body = """
        {
            "context": "vessels.urn:mrn:imo:mmsi:123",
            "range": {"from":"2026-04-23T14:00:00Z","to":"2026-04-23T15:00:00Z"},
            "values": [{"path":"navigation.position","method":"first"}],
            "data": [
                ["2026-04-23T14:00:00Z", [-76.82, 24.60]],
                ["2026-04-23T14:00:30Z", [-76.83, 24.61]]
            ]
        }
        """;
        var pts = await HistoryOnlyApi(body).GetServerTrackAsync("1h");

        await Assert.That(pts).IsNotNull();
        await Assert.That(pts!.Length).IsEqualTo(2);
        // [lat, lon] for Leaflet; [lon, lat] swap from the source.
        await Assert.That(pts[0][0]).IsEqualTo(24.60);
        await Assert.That(pts[0][1]).IsEqualTo(-76.82);
    }

    [Test]
    public async Task History_API_Object_Value_Shape_Parses()
    {
        // Some influx-backed history plugins emit {latitude,
        // longitude} instead of the bare array. Both shapes must
        // parse so the History page doesn't care which plugin the
        // server has.
        string body = """
        {
            "data": [
                ["2026-04-23T14:00:00Z", {"latitude": 47.40, "longitude": 8.50}],
                ["2026-04-23T14:00:30Z", {"latitude": 47.41, "longitude": 8.51}]
            ]
        }
        """;
        var pts = await HistoryOnlyApi(body).GetServerTrackAsync("1h");

        await Assert.That(pts).IsNotNull();
        await Assert.That(pts!.Length).IsEqualTo(2);
        await Assert.That(pts[0][0]).IsEqualTo(47.40);
        await Assert.That(pts[0][1]).IsEqualTo(8.50);
    }

    [Test]
    public async Task History_API_Skips_Null_And_Malformed_Entries()
    {
        // Gaps in the recording (GPS outage) surface as null values
        // at aggregated timestamps. Skip them rather than crash or
        // inject a zero-island into the track.
        string body = """
        {
            "data": [
                ["2026-04-23T14:00:00Z", [-76.82, 24.60]],
                ["2026-04-23T14:00:15Z", null],
                ["2026-04-23T14:00:30Z", [-76.83]],
                ["2026-04-23T14:00:45Z", [-76.84, 24.62]]
            ]
        }
        """;
        var pts = await HistoryOnlyApi(body).GetServerTrackAsync("1h");

        await Assert.That(pts).IsNotNull();
        await Assert.That(pts!.Length).IsEqualTo(2);
    }

    [Test]
    public async Task History_API_Builds_ISO8601_Duration_From_Shorthand()
    {
        // Pin that "1h" -> PT1H, "1d" -> P1D, and that the
        // History API URL carries BOTH paths+duration+resolution
        // query params so a signalk-parquet plugin actually
        // matches the route.
        string? capturedQuery = null;
        string body = """{"data":[["2026-04-23T14:00:00Z",[-76,24]]]}""";
        var api = HistoryOnlyApi(body, req => { capturedQuery = req.RequestUri?.Query; });

        await api.GetServerTrackAsync("1h", "30s");
        await Assert.That(capturedQuery).IsNotNull();
        await Assert.That(capturedQuery!).Contains("paths=navigation.position");
        await Assert.That(capturedQuery).Contains("duration=PT1H");
        await Assert.That(capturedQuery).Contains("resolution=30s");

        await api.GetServerTrackAsync("3d", "1m");
        await Assert.That(capturedQuery).Contains("duration=P3D");
    }

    [Test]
    public async Task ToIsoDuration_Matrix()
    {
        await Assert.That(TrackApi.ToIsoDuration("1h")).IsEqualTo("PT1H");
        await Assert.That(TrackApi.ToIsoDuration("6h")).IsEqualTo("PT6H");
        await Assert.That(TrackApi.ToIsoDuration("1d")).IsEqualTo("P1D");
        await Assert.That(TrackApi.ToIsoDuration("7d")).IsEqualTo("P7D");
        // Already-ISO passes through.
        await Assert.That(TrackApi.ToIsoDuration("PT15M")).IsEqualTo("PT15M");
        // Garbage falls back to 1 day so an unknown dropdown value
        // doesn't return zero-window (no matches) silently.
        await Assert.That(TrackApi.ToIsoDuration("")).IsEqualTo("P1D");
        await Assert.That(TrackApi.ToIsoDuration("garbage")).IsEqualTo("P1D");
    }

    [Test]
    public async Task V1_Success_Does_Not_Fall_Through_To_V2_Tracks()
    {
        // If v1 /self/track returns data, the v2 /resources/tracks
        // fallback should never be queried. Pin the short-circuit
        // so a future refactor doesn't accidentally merge both
        // surfaces and double-count points on servers that expose
        // both. History API is the new primary source and runs
        // first; a 404 there is expected in this test because
        // we're simulating a "v1-tracks-only" install.
        int historyCalls = 0, v1Calls = 0, v2TracksCalls = 0;
        var http = ApiTestHelpers.MockClient(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("/history/values", StringComparison.Ordinal))
            {
                historyCalls++;
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
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
            v2TracksCalls++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var api = new TrackApi(http, ApiTestHelpers.FixedBaseUrl());

        var pts = await api.GetServerTrackAsync();
        await Assert.That(pts).IsNotNull();
        await Assert.That(pts!.Length).IsEqualTo(2);
        await Assert.That(historyCalls).IsEqualTo(1);
        await Assert.That(v1Calls).IsEqualTo(1);
        await Assert.That(v2TracksCalls).IsEqualTo(0);
    }

    [Test]
    public async Task History_Success_Short_Circuits_Tracks_Fallbacks()
    {
        // The happy path: History API returns data, neither v1 nor
        // v2 tracks is queried. Avoids double-counting on installs
        // that expose multiple history surfaces (signalk-parquet
        // plus @signalk/tracks).
        int historyCalls = 0, v1Calls = 0, v2TracksCalls = 0;
        var http = ApiTestHelpers.MockClient(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("/history/values", StringComparison.Ordinal))
            {
                historyCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"data":[["2026-04-23T14:00:00Z",[-76.82,24.60]]]}""",
                        System.Text.Encoding.UTF8, "application/json"),
                };
            }
            if (path.Contains("/v1/api/self/track", StringComparison.Ordinal)) v1Calls++;
            else v2TracksCalls++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var api = new TrackApi(http, ApiTestHelpers.FixedBaseUrl());

        var pts = await api.GetServerTrackAsync();
        await Assert.That(pts).IsNotNull();
        await Assert.That(pts!.Length).IsEqualTo(1);
        await Assert.That(historyCalls).IsEqualTo(1);
        await Assert.That(v1Calls).IsEqualTo(0);
        await Assert.That(v2TracksCalls).IsEqualTo(0);
    }
}
