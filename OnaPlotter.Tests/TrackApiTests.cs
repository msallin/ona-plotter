using System.Net;
using OnaPlotter.Models;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the parsing contract for the SignalK History API v2
/// (<c>/signalk/v2/api/history/values</c>). TrackApi is the thin
/// shim the History page uses; after the cleanup it only knows
/// about the History API.
///
/// Earlier versions tried the v1 /self/track and v2 /resources/tracks
/// endpoints as fallbacks, but those returned fixed-cadence data
/// and every openplotter / modern signalk-server install exposes
/// the history surface via signalk-parquet, so the fallbacks were
/// dead weight. Tests for the dropped code went with them.
/// </summary>
public class TrackApiTests
{
    /// <summary>Helper: the history endpoint returns the given body,
    /// everything else 404s. Reflects the reality that TrackApi now
    /// ONLY talks to /history/values.</summary>
    private static TrackApi HistoryApi(string historyBody,
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
    public async Task History_Array_Value_Shape_Parses()
    {
        // signalk-parquet default: position value is [lon, lat].
        // Pin the parse + axis swap so a palette tweak doesn't
        // silently render the track mirrored across the meridian.
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
        var pts = await HistoryApi(body).GetServerTrackAsync("1h");

        await Assert.That(pts).IsNotNull();
        await Assert.That(pts!.Length).IsEqualTo(2);
        // [lat, lon] for Leaflet; swapped from the [lon, lat] source.
        await Assert.That(pts[0][0]).IsEqualTo(24.60);
        await Assert.That(pts[0][1]).IsEqualTo(-76.82);
    }

    [Test]
    public async Task History_Object_Value_Shape_Parses()
    {
        // Some influx-backed history plugins emit {latitude,
        // longitude} instead of the bare array. Both shapes must
        // parse so the History page doesn't care which plugin the
        // server has configured.
        string body = """
        {
            "data": [
                ["2026-04-23T14:00:00Z", {"latitude": 47.40, "longitude": 8.50}],
                ["2026-04-23T14:00:30Z", {"latitude": 47.41, "longitude": 8.51}]
            ]
        }
        """;
        var pts = await HistoryApi(body).GetServerTrackAsync("1h");

        await Assert.That(pts).IsNotNull();
        await Assert.That(pts!.Length).IsEqualTo(2);
        await Assert.That(pts[0][0]).IsEqualTo(47.40);
        await Assert.That(pts[0][1]).IsEqualTo(8.50);
    }

    [Test]
    public async Task History_Skips_Null_And_Malformed_Entries()
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
        var pts = await HistoryApi(body).GetServerTrackAsync("1h");

        await Assert.That(pts).IsNotNull();
        await Assert.That(pts!.Length).IsEqualTo(2);
    }

    [Test]
    public async Task History_Builds_ISO8601_Query_Params_From_Shorthand()
    {
        // Pin that "1h" -> PT1H, "1d" -> P1D, and that the History
        // API URL carries paths + duration + resolution query params
        // as the spec demands. A plugin that ignores the resolution
        // parameter would return recorder-cadence data; a plugin
        // that ignores the path param would return nothing.
        string? capturedQuery = null;
        string body = """{"data":[["2026-04-23T14:00:00Z",[-76,24]]]}""";
        var api = HistoryApi(body, req => { capturedQuery = req.RequestUri?.Query; });

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
    public async Task History_Empty_Data_Returns_Null()
    {
        // Provider installed but no data in the window -> null so
        // the History page shows the "no track data" hint instead
        // of an empty map.
        var pts = await HistoryApi("""{"data":[]}""").GetServerTrackAsync("1h");
        await Assert.That(pts).IsNull();
    }

    [Test]
    public async Task History_Missing_Endpoint_Returns_Null()
    {
        // No history provider installed -> /history/values returns
        // 404. Used to fall through to track endpoints; now just
        // returns null because we no longer carry those fallbacks.
        var http = ApiTestHelpers.MockClient(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound));
        var api = new TrackApi(http, ApiTestHelpers.FixedBaseUrl());
        var pts = await api.GetServerTrackAsync("1h");
        await Assert.That(pts).IsNull();
    }

    // -----------------------------------------------------------------
    // GetServerTrackPointsAsync: rich multi-path fetch with timestamps.
    // Powers the History-page segmenter and the future stats page.
    // -----------------------------------------------------------------

    [Test]
    public async Task RichFetch_ParsesAllSupportedColumns()
    {
        // Every path the rich query asks for present in `values`,
        // every cell populated. The TrackPoint should carry SOG, COG,
        // heading, TWS, TWA -- all from the per-row column indices the
        // server reported via the `values` array.
        string body = """
        {
            "context": "vessels.urn:mrn:imo:mmsi:123",
            "values": [
                {"path":"navigation.position","method":"first"},
                {"path":"navigation.speedOverGround","method":"average"},
                {"path":"navigation.courseOverGroundTrue","method":"average"},
                {"path":"navigation.headingTrue","method":"average"},
                {"path":"environment.wind.speedTrue","method":"average"},
                {"path":"environment.wind.angleTrueWater","method":"average"}
            ],
            "data": [
                ["2026-04-23T14:00:00Z", [-76.82, 24.60], 2.5, 1.5708, 1.5707, 5.0, 0.7],
                ["2026-04-23T14:00:30Z", [-76.83, 24.61], 3.1, 1.5710, 1.5709, 6.0, 0.8]
            ]
        }
        """;
        var pts = await HistoryApi(body)
            .GetServerTrackPointsAsync(from: null, to: null, timespan: "1h");

        await Assert.That(pts).IsNotNull();
        await Assert.That(pts!.Length).IsEqualTo(2);
        var p = pts[0];
        await Assert.That(p.Latitude).IsEqualTo(24.60);
        await Assert.That(p.Longitude).IsEqualTo(-76.82);
        await Assert.That(p.SpeedOverGround).IsEqualTo(2.5);
        await Assert.That(p.CourseOverGround).IsEqualTo(1.5708);
        await Assert.That(p.Heading).IsEqualTo(1.5707);
        await Assert.That(p.WindSpeedTrue).IsEqualTo(5.0);
        await Assert.That(p.WindAngleTrue).IsEqualTo(0.7);
        await Assert.That(p.Timestamp.ToString("o")).Contains("2026-04-23T14:00:00");
    }

    [Test]
    public async Task RichFetch_ServerOmitsUnsupportedPaths_LeavesFieldsNull()
    {
        // Server's history provider doesn't have wind paths configured.
        // The `values` array reflects what's actually being returned;
        // the parser must not assume positional alignment with what we
        // ASKED for. Only position + sog come back; the TrackPoint's
        // wind fields stay null.
        string body = """
        {
            "values": [
                {"path":"navigation.position","method":"first"},
                {"path":"navigation.speedOverGround","method":"average"}
            ],
            "data": [
                ["2026-04-23T14:00:00Z", [-76.82, 24.60], 2.5]
            ]
        }
        """;
        var pts = await HistoryApi(body)
            .GetServerTrackPointsAsync(from: null, to: null, timespan: "1h");

        await Assert.That(pts).IsNotNull();
        await Assert.That(pts!.Length).IsEqualTo(1);
        await Assert.That(pts[0].SpeedOverGround).IsEqualTo(2.5);
        await Assert.That(pts[0].WindSpeedTrue).IsNull();
        await Assert.That(pts[0].CourseOverGround).IsNull();
    }

    [Test]
    public async Task RichFetch_ServerReordersColumns_ParserUsesValuesArray()
    {
        // Server picks a different column order than the request.
        // Without the `values` lookup, positional indexing into `data`
        // would slot SOG into the COG slot. Pinned because the parser
        // explicitly walks `values` to learn the column-to-path mapping.
        string body = """
        {
            "values": [
                {"path":"environment.wind.speedTrue","method":"average"},
                {"path":"navigation.position","method":"first"},
                {"path":"navigation.speedOverGround","method":"average"}
            ],
            "data": [
                ["2026-04-23T14:00:00Z", 5.0, [-76.82, 24.60], 2.5]
            ]
        }
        """;
        var pts = await HistoryApi(body)
            .GetServerTrackPointsAsync(from: null, to: null, timespan: "1h");

        await Assert.That(pts).IsNotNull();
        await Assert.That(pts!.Length).IsEqualTo(1);
        // SOG must come from column 3 (per `values`), not column 1.
        await Assert.That(pts[0].SpeedOverGround).IsEqualTo(2.5);
        await Assert.That(pts[0].WindSpeedTrue).IsEqualTo(5.0);
        await Assert.That(pts[0].Latitude).IsEqualTo(24.60);
    }

    [Test]
    public async Task RichFetch_PositionMissing_ReturnsNull()
    {
        // No position path = no track. Without a fix there's nothing
        // to plot regardless of how much SOG / wind data the server has.
        string body = """
        {
            "values": [
                {"path":"navigation.speedOverGround","method":"average"}
            ],
            "data": [["2026-04-23T14:00:00Z", 2.5]]
        }
        """;
        var pts = await HistoryApi(body)
            .GetServerTrackPointsAsync(from: null, to: null, timespan: "1h");

        await Assert.That(pts).IsNull();
    }

    [Test]
    public async Task RichFetch_SkipsRowWithNullPosition()
    {
        // Position null on a row = GPS outage at that aggregation tick.
        // Skip that row (no fix to plot) but keep neighbouring rows
        // that DO have a position.
        string body = """
        {
            "values": [
                {"path":"navigation.position","method":"first"},
                {"path":"navigation.speedOverGround","method":"average"}
            ],
            "data": [
                ["2026-04-23T14:00:00Z", [-76.82, 24.60], 2.5],
                ["2026-04-23T14:00:30Z", null, 2.6],
                ["2026-04-23T14:01:00Z", [-76.83, 24.61], 2.7]
            ]
        }
        """;
        var pts = await HistoryApi(body)
            .GetServerTrackPointsAsync(from: null, to: null, timespan: "1h");

        await Assert.That(pts).IsNotNull();
        await Assert.That(pts!.Length).IsEqualTo(2);
    }

    [Test]
    public async Task RichFetch_BuildsAbsoluteWindowQuery_When_From_Provided()
    {
        // Helm picks an absolute date range -> the URL must carry
        // from + to in ISO 8601 + UTC, NOT a relative duration. Pin
        // both ends so a future refactor can't accidentally drop one.
        // Window deliberately stays well under the chunking cap
        // (500 × 30s = 4 h) so this test exercises the single-
        // request path; pagination is covered by its own tests.
        string? capturedQuery = null;
        string body = """{"values":[{"path":"navigation.position","method":"first"}],"data":[["2026-04-23T14:00:00Z",[-76,24]]]}""";
        var api = HistoryApi(body, req => { capturedQuery = req.RequestUri?.Query; });

        await api.GetServerTrackPointsAsync(
            from: new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero),
            to: new DateTimeOffset(2026, 4, 1, 1, 0, 0, TimeSpan.Zero),
            timespan: null);

        await Assert.That(capturedQuery).IsNotNull();
        await Assert.That(capturedQuery!).Contains("from=");
        await Assert.That(capturedQuery).Contains("to=");
        // Relative duration must not be present when the absolute
        // form is used; mixing the two would produce an undefined
        // server response.
        await Assert.That(capturedQuery!.Contains("duration=")).IsFalse();
        // Timestamp shape: ISO 8601 with 'Z' suffix (UTC). URL-encoded
        // colons appear as %3A.
        await Assert.That(capturedQuery).Contains("2026-04-01T00%3A00%3A00");
    }

    [Test]
    public async Task RichFetch_BuildsRelativeWindowQuery_When_From_Null()
    {
        // No absolute window supplied -> falls back to duration. This
        // is the History-page default-load path (timespan dropdown).
        // 1 h at 30 s = 120 rows, comfortably under the 500-row cap,
        // so the single-request path with `duration=` is preserved
        // (paginated windows convert to absolute from / to chunks).
        string? capturedQuery = null;
        string body = """{"values":[{"path":"navigation.position","method":"first"}],"data":[["2026-04-23T14:00:00Z",[-76,24]]]}""";
        var api = HistoryApi(body, req => { capturedQuery = req.RequestUri?.Query; });

        await api.GetServerTrackPointsAsync(from: null, to: null, timespan: "1h");

        await Assert.That(capturedQuery).IsNotNull();
        await Assert.That(capturedQuery!).Contains("duration=PT1H");
        await Assert.That(capturedQuery!.Contains("from=")).IsFalse();
    }

    [Test]
    public async Task RichFetch_BboxParam_Appends_SouthWestNorthEast_InvariantCulture()
    {
        // Pin: the bbox query string is ordered south,west,north,east
        // (signalk-parquet's bbox extension), and the doubles render
        // with '.' decimals regardless of ambient locale. A de-CH or
        // fr-FR helm running this code with the locale picking up
        // ',' as the decimal would otherwise smuggle a value the
        // server can't parse. This is a defensive assertion: the
        // implementation already pins InvariantCulture, the test
        // catches a future drift.
        string? capturedQuery = null;
        string body = """{"values":[{"path":"navigation.position","method":"first"}],"data":[["2026-04-23T14:00:00Z",[-76,24]]]}""";
        var api = HistoryApi(body, req => { capturedQuery = req.RequestUri?.Query; });

        var bbox = new TrackBbox(South: 47.30, West: 8.40, North: 47.50, East: 8.60);
        await api.GetServerTrackPointsAsync(
            from: null, to: null, timespan: "1h", bbox: bbox);

        await Assert.That(capturedQuery).IsNotNull();
        // URL-encoded comma is %2C; the order in the query value is
        // s,w,n,e. Pin the formatted values as substrings -- a future
        // serialiser change that adds extra precision (47.300000)
        // shouldn't break the test, hence the substring matches.
        await Assert.That(capturedQuery!).Contains("bbox=47.3");
        await Assert.That(capturedQuery).Contains("8.4");
        await Assert.That(capturedQuery).Contains("47.5");
        await Assert.That(capturedQuery).Contains("8.6");
        // No comma-decimals snuck in.
        await Assert.That(capturedQuery!.Contains("47%2C3")).IsFalse();
    }

    [Test]
    public async Task RichFetch_NoBbox_OmitsBboxParam()
    {
        // Default behaviour without a bbox: don't add the parameter.
        // Some servers reject unknown params strictly; sending an
        // empty bbox= would hit that. Pin "absent when null".
        string? capturedQuery = null;
        string body = """{"values":[{"path":"navigation.position","method":"first"}],"data":[["2026-04-23T14:00:00Z",[-76,24]]]}""";
        var api = HistoryApi(body, req => { capturedQuery = req.RequestUri?.Query; });

        await api.GetServerTrackPointsAsync(from: null, to: null, timespan: "1h");

        await Assert.That(capturedQuery).IsNotNull();
        await Assert.That(capturedQuery!.Contains("bbox=")).IsFalse();
    }

    [Test]
    public async Task RichFetch_MultiPathsParameter_Includes_Sog_And_Wind()
    {
        // The query must ask the server for the rich path set, not
        // just position. A regression that drops sog from `paths`
        // would yield a fast/silent fallback to position-only and
        // the segmenter would have to rely on inter-sample distance
        // for every classification (loss of fidelity).
        string? capturedQuery = null;
        string body = """{"values":[{"path":"navigation.position","method":"first"}],"data":[["2026-04-23T14:00:00Z",[-76,24]]]}""";
        var api = HistoryApi(body, req => { capturedQuery = req.RequestUri?.Query; });

        await api.GetServerTrackPointsAsync(from: null, to: null, timespan: "1h");

        await Assert.That(capturedQuery).IsNotNull();
        await Assert.That(capturedQuery!).Contains("navigation.position");
        await Assert.That(capturedQuery).Contains("navigation.speedOverGround");
        await Assert.That(capturedQuery).Contains("environment.wind.speedTrue");
    }

    [Test]
    public async Task RichFetch_Timestamp_PinsUtcKind_AndOffsetConvertedToUtc()
    {
        // Pin: TrackPoint.Timestamp is UTC (Kind=Utc), AND a server
        // emitting the ISO timestamp with an explicit non-zero offset
        // (e.g. "+02:00") parses to the corresponding UTC instant
        // rather than being kept as local. AdjustToUniversal is what
        // makes this work; the substring-based test in earlier rounds
        // didn't catch a regression that dropped that flag.
        string body = """
        {
            "values":[{"path":"navigation.position","method":"first"}],
            "data":[["2026-04-23T16:00:00+02:00",[-76,24]]]
        }
        """;
        var pts = await HistoryApi(body)
            .GetServerTrackPointsAsync(from: null, to: null, timespan: "1h");

        await Assert.That(pts).IsNotNull();
        await Assert.That(pts!.Length).IsEqualTo(1);
        await Assert.That(pts[0].Timestamp.Kind).IsEqualTo(DateTimeKind.Utc);
        // 16:00+02:00 = 14:00 UTC.
        await Assert.That(pts[0].Timestamp).IsEqualTo(
            new DateTime(2026, 4, 23, 14, 0, 0, DateTimeKind.Utc));
    }

    [Test]
    public async Task RichFetch_TransportFailure_ReturnsNull()
    {
        // ResourceHttp catches HttpRequestException internally on the
        // legacy GetServerTrackAsync path; the rich path has its own
        // try/catch. Pin both the catch and the null return so a
        // refactor that drops the wrapper goes red.
        var http = ApiTestHelpers.MockClient(_ => throw new HttpRequestException("dns fail"));
        var api = new TrackApi(http, ApiTestHelpers.FixedBaseUrl());
        var pts = await api.GetServerTrackPointsAsync(from: null, to: null, timespan: "1h");
        await Assert.That(pts).IsNull();
    }

    [Test]
    public async Task RichFetch_5xx_ReturnsNull()
    {
        // 5xx from the history provider plugin (e.g. signalk-parquet
        // hitting a corrupt parquet file). History page falls back to
        // the empty-state hint instead of trying to render undefined
        // data.
        var http = ApiTestHelpers.MockClient(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var api = new TrackApi(http, ApiTestHelpers.FixedBaseUrl());
        var pts = await api.GetServerTrackPointsAsync(from: null, to: null, timespan: "1h");
        await Assert.That(pts).IsNull();
    }

    [Test]
    public async Task RichFetch_EmptyData_ReturnsNull()
    {
        // Provider installed but no data in the window. Same fallback
        // shape as legacy GetServerTrackAsync; pinned independently
        // here so a regression on the rich path can't escape.
        string body = """
        {
            "values":[{"path":"navigation.position","method":"first"}],
            "data":[]
        }
        """;
        var pts = await HistoryApi(body)
            .GetServerTrackPointsAsync(from: null, to: null, timespan: "1h");
        await Assert.That(pts).IsNull();
    }

    [Test]
    public async Task RichFetch_BboxParam_HasExactSouthWestNorthEastOrder()
    {
        // Belt-and-braces over the substring assertions in
        // RichFetch_BboxParam_Appends_SouthWestNorthEast_InvariantCulture:
        // unescape the bbox param and pin the EXACT value. A regression
        // that swapped to GeoJSON order (west,south,east,north) would
        // still hit the substring matches; this catches it.
        string? capturedQuery = null;
        string body = """{"values":[{"path":"navigation.position","method":"first"}],"data":[["2026-04-23T14:00:00Z",[-76,24]]]}""";
        var api = HistoryApi(body, req => { capturedQuery = req.RequestUri?.Query; });

        var bbox = new TrackBbox(South: 47.30, West: 8.40, North: 47.50, East: 8.60);
        await api.GetServerTrackPointsAsync(from: null, to: null, timespan: "1h", bbox: bbox);

        await Assert.That(capturedQuery).IsNotNull();
        var match = System.Text.RegularExpressions.Regex.Match(capturedQuery!, "bbox=([^&]+)");
        await Assert.That(match.Success).IsTrue();
        var raw = Uri.UnescapeDataString(match.Groups[1].Value);
        await Assert.That(raw).IsEqualTo("47.3,8.4,47.5,8.6");
    }

    // -----------------------------------------------------------------
    // Pagination: signalk-parquet caps each response at 500 rows. For
    // long windows (e.g. 7 d at 30 s = 20 160 expected rows) we split
    // the request into chunks so each sub-request stays inside the
    // cap and the server returns the requested resolution unaltered.
    // -----------------------------------------------------------------

    [Test]
    public async Task RichFetch_LargeWindow_AbsoluteRange_PaginatesIntoMultipleRequests()
    {
        // Helm picks 7 d at 30 s. Without chunking the response would
        // be silently re-bucketed by the server, leaving ~20-min
        // sample gaps that the segmenter then surfaces as fictitious
        // 20-min holes between trips. Pin the chunked URL count so a
        // refactor that drops the loop goes red.
        var queries = new List<string>();
        string body = """{"values":[{"path":"navigation.position","method":"first"}],"data":[["2026-04-23T14:00:00Z",[-76,24]]]}""";
        var api = HistoryApi(body, req =>
        {
            if (req.RequestUri?.Query is string q) queries.Add(q);
        });

        await api.GetServerTrackPointsAsync(
            from: new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero),
            to: new DateTimeOffset(2026, 4, 8, 0, 0, 0, TimeSpan.Zero),
            timespan: null,
            resolution: "30s");

        // Chunk width = 500 × 0.95 × 30 s = 14 250 s ≈ 3.96 h. Over
        // a 7-day window that's ~42 chunks. Anything in [40, 50] is
        // fine; pinning a tight upper / lower bound keeps the test
        // honest without coupling to the safety fraction's exact value.
        await Assert.That(queries.Count).IsGreaterThan(40);
        await Assert.That(queries.Count).IsLessThan(50);

        // Every chunk uses absolute from/to (NOT relative duration);
        // the relative form is what gets the cap applied server-side
        // because the plugin can't predict bucket counts without the
        // explicit bounds. Belt-and-braces vs the sub-window assertion.
        foreach (var q in queries)
        {
            await Assert.That(q.Contains("from=")).IsTrue();
            await Assert.That(q.Contains("to=")).IsTrue();
            await Assert.That(q.Contains("duration=")).IsFalse();
        }
    }

    [Test]
    public async Task RichFetch_LargeWindow_RelativeTimespan_AlsoPaginates()
    {
        // Relative window over the cap (e.g. 7 d at 30 s) collapses
        // to absolute "now-relative" chunks. Without that conversion
        // the server bucket-decimates the same way the absolute path
        // would; the helm sees the same trip-table holes. Pin the
        // multi-request shape for the relative path too because
        // History.razor's preset dropdown emits this code path.
        int requestCount = 0;
        string body = """{"values":[{"path":"navigation.position","method":"first"}],"data":[["2026-04-23T14:00:00Z",[-76,24]]]}""";
        var api = HistoryApi(body, _ => requestCount++);

        await api.GetServerTrackPointsAsync(
            from: null, to: null,
            timespan: "7d",
            resolution: "30s");

        // Same arithmetic as the absolute test; pinned independently
        // because the relative-to-absolute conversion runs through a
        // different code path inside TryComputeWindowSpan.
        await Assert.That(requestCount).IsGreaterThan(40);
    }

    [Test]
    public async Task RichFetch_SmallWindow_BothTimespanAndAbsolute_StaysSingleRequest()
    {
        // Belt-and-braces against an over-eager paginator: 1 h at 30 s
        // = 120 rows, comfortably under the 500-row cap. Both shapes
        // (timespan and absolute) must hit the server exactly once
        // each. The single-request fast path keeps URL shapes
        // unchanged for callers that pinned them.
        int absoluteCalls = 0;
        int relativeCalls = 0;
        string body = """{"values":[{"path":"navigation.position","method":"first"}],"data":[["2026-04-23T14:00:00Z",[-76,24]]]}""";
        var api1 = HistoryApi(body, _ => absoluteCalls++);
        var api2 = HistoryApi(body, _ => relativeCalls++);

        await api1.GetServerTrackPointsAsync(
            from: new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero),
            to: new DateTimeOffset(2026, 4, 1, 1, 0, 0, TimeSpan.Zero),
            timespan: null);
        await api2.GetServerTrackPointsAsync(
            from: null, to: null, timespan: "1h");

        await Assert.That(absoluteCalls).IsEqualTo(1);
        await Assert.That(relativeCalls).IsEqualTo(1);
    }

    [Test]
    public async Task RichFetch_PaginatedChunks_ConcatenateAndDedupeBoundary()
    {
        // The seam between two chunks: SignalK's `from`/`to` are
        // documented inclusive on both ends, so a sample at the seam
        // instant comes back in BOTH the previous chunk's tail and
        // the next chunk's head. The aggregator must skip the
        // duplicated head sample so the segmenter doesn't see a
        // zero-distance "hop" right at the seam (which would emit a
        // 1-point segment on every chunk boundary).
        // Pick a window + resolution that produces EXACTLY two chunks
        // so the assertion can be precise. Chunk width = 500 × 0.95 ×
        // resolution = 475 × resolution. Resolution 1 s → 475 s wide
        // chunks; window 950 s spans two chunks. The dedup contract:
        // chunk-1's last sample timestamp matches chunk-2's first
        // sample timestamp, so the aggregator drops the head of
        // chunk 2 to keep the segmenter from seeing a 1-point seam
        // segment. Empty response for any further chunk so a
        // miscalculation in chunk count doesn't silently inflate
        // the asserted total.
        int callIndex = 0;
        var bodies = new[]
        {
            // Chunk 1: two samples; the second is the seam.
            """{"values":[{"path":"navigation.position","method":"first"}],"data":[["2026-04-01T00:00:00Z",[-76,24]],["2026-04-01T00:07:55Z",[-76,24.01]]]}""",
            // Chunk 2: starts AT the seam (duplicate ts), then a fresh sample.
            """{"values":[{"path":"navigation.position","method":"first"}],"data":[["2026-04-01T00:07:55Z",[-76,24.01]],["2026-04-01T00:15:00Z",[-76,24.02]]]}""",
            // Any further chunk: empty so unexpected extra calls
            // can't pad the result and mask a logic error.
            """{"values":[{"path":"navigation.position","method":"first"}],"data":[]}""",
        };
        var http = ApiTestHelpers.MockClient(req =>
        {
            var body = bodies[Math.Min(callIndex++, bodies.Length - 1)];
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
        });
        var api = new TrackApi(http, ApiTestHelpers.FixedBaseUrl());

        // 950 s @ 1 s = 950 expected rows. Cap × safety = 500 × 0.95
        // = 475 → two chunks; 950 > 475 triggers pagination.
        var pts = await api.GetServerTrackPointsAsync(
            from: new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero),
            to: new DateTimeOffset(2026, 4, 1, 0, 15, 50, TimeSpan.Zero),
            timespan: null, resolution: "1s");

        await Assert.That(pts).IsNotNull();
        // 2 chunks × 2 samples each = 4 raw, minus 1 seam dupe = 3.
        await Assert.That(pts!.Length).IsEqualTo(3);
        await Assert.That(pts[0].Timestamp).IsEqualTo(
            new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc));
        await Assert.That(pts[1].Timestamp).IsEqualTo(
            new DateTime(2026, 4, 1, 0, 7, 55, DateTimeKind.Utc));
        await Assert.That(pts[2].Timestamp).IsEqualTo(
            new DateTime(2026, 4, 1, 0, 15, 0, DateTimeKind.Utc));
    }

    [Test]
    public async Task TryParseResolutionToSpan_AcceptsShorthandAndIsoForms()
    {
        // Pinned because the chunking decision relies on this helper
        // returning a positive TimeSpan; an unparseable string falls
        // back to a single request. Keep both forms parsing so a
        // future caller wiring `PT30S` directly stays supported.
        await Assert.That(TrackApi.TryParseResolutionToSpan("30s")).IsEqualTo(TimeSpan.FromSeconds(30));
        await Assert.That(TrackApi.TryParseResolutionToSpan("5m")).IsEqualTo(TimeSpan.FromMinutes(5));
        await Assert.That(TrackApi.TryParseResolutionToSpan("2h")).IsEqualTo(TimeSpan.FromHours(2));
        await Assert.That(TrackApi.TryParseResolutionToSpan("1d")).IsEqualTo(TimeSpan.FromDays(1));
        await Assert.That(TrackApi.TryParseResolutionToSpan("PT30S")).IsEqualTo(TimeSpan.FromSeconds(30));
        await Assert.That(TrackApi.TryParseResolutionToSpan("")).IsNull();
        await Assert.That(TrackApi.TryParseResolutionToSpan("nonsense")).IsNull();
    }

    // -----------------------------------------------------------------
    // Cache integration: paginated chunks consult HistoryCache. First
    // load fills the cache; second load with the same window reuses
    // every chunk (zero HTTP requests on warm cache). Live-tail
    // chunks (within 1 min of "now") refetch every time.
    // -----------------------------------------------------------------

    /// <summary>Frozen-clock TimeProvider so the live-tail rule fires
    /// deterministically in cache integration tests. Same shape as
    /// the one in <c>HistoryCacheTests</c>; duplicated here rather
    /// than promoted to a shared helper because the two test classes
    /// only have this one type in common, and a shared file would
    /// hide the pin from a future reader.</summary>
    private sealed class FrozenTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Paginating TrackApi wired to a fresh cache + a request
    /// counter. The mock returns the supplied body for every chunk,
    /// which is fine for cache-behaviour assertions where we only
    /// care about HOW MANY times the server was hit, not which
    /// chunks came back with which data.</summary>
    private static (TrackApi api, HistoryCache cache, Func<int> getRequestCount)
        CachedHistoryApi(string body, FrozenTime time)
    {
        int count = 0;
        var http = ApiTestHelpers.MockClient(req =>
        {
            count++;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
        });
        var cache = new HistoryCache(time);
        var api = new TrackApi(http, ApiTestHelpers.FixedBaseUrl(), cache);
        return (api, cache, () => count);
    }

    [Test]
    public async Task Cache_FirstLoad_PopulatesCache_SecondLoad_ServesFromCache()
    {
        // Frozen at a "now" far past the queried window so every
        // chunk is OUTSIDE the live-tail freshness band. Otherwise
        // the cache's HeadFreshness rule would refuse to store the
        // chunks and the second load would still hit the server.
        var time = new FrozenTime(DateTimeOffset.Parse("2030-01-01T00:00:00Z"));
        // Two-sample body; passes the segmenter contract but the
        // cache test only cares about request count.
        string body = """{"values":[{"path":"navigation.position","method":"first"}],"data":[["2026-04-25T12:00:00Z",[-76.0,24.0]],["2026-04-25T12:00:30Z",[-76.0,24.01]]]}""";
        var (api, cache, getCount) = CachedHistoryApi(body, time);

        // 950 s @ 1 s = 950 expected rows -> 2 chunks at the cap.
        var window = (
            from: new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero),
            to: new DateTimeOffset(2026, 4, 25, 12, 15, 50, TimeSpan.Zero));

        var first = await api.GetServerTrackPointsAsync(
            from: window.from, to: window.to, timespan: null, resolution: "1s");

        int afterFirst = getCount();
        await Assert.That(first).IsNotNull();
        await Assert.That(afterFirst).IsEqualTo(2);
        await Assert.That(cache.Count).IsEqualTo(2);
        await Assert.That(cache.Hits).IsEqualTo(0);
        await Assert.That(cache.Misses).IsGreaterThanOrEqualTo(2);

        // Second load with the EXACT same window: every chunk is
        // already cached. Request count must NOT advance, and the
        // cache hit counter must rise by the chunk count.
        var second = await api.GetServerTrackPointsAsync(
            from: window.from, to: window.to, timespan: null, resolution: "1s");

        int afterSecond = getCount();
        await Assert.That(second).IsNotNull();
        await Assert.That(afterSecond)
            .IsEqualTo(afterFirst)
            .Because("warm cache must serve every chunk without hitting the server");
        await Assert.That(cache.Hits).IsGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task Cache_DifferentResolution_Misses()
    {
        // Resolution is part of the cache key. Re-issuing the same
        // window at a different resolution must NOT serve cached
        // 30s chunks as if they were 5m chunks (the buckets are
        // different and the server's response would differ).
        var time = new FrozenTime(DateTimeOffset.Parse("2030-01-01T00:00:00Z"));
        string body = """{"values":[{"path":"navigation.position","method":"first"}],"data":[["2026-04-25T12:00:00Z",[-76.0,24.0]],["2026-04-25T12:00:30Z",[-76.0,24.01]]]}""";
        var (api, cache, getCount) = CachedHistoryApi(body, time);
        var window = (
            from: new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero),
            to: new DateTimeOffset(2026, 4, 25, 12, 15, 50, TimeSpan.Zero));

        await api.GetServerTrackPointsAsync(
            from: window.from, to: window.to, timespan: null, resolution: "1s");
        int afterFirst = getCount();

        // Same window, different resolution: must hit the server again.
        await api.GetServerTrackPointsAsync(
            from: window.from, to: window.to, timespan: null, resolution: "5s");

        int afterSecond = getCount();
        await Assert.That(afterSecond - afterFirst).IsGreaterThan(0);
    }

    [Test]
    public async Task Cache_DifferentBbox_Misses()
    {
        // bbox is part of the cache key. A re-load with a different
        // bbox (e.g. helm panned the History map) must refetch
        // because the server may return different points for the
        // narrowed area.
        var time = new FrozenTime(DateTimeOffset.Parse("2030-01-01T00:00:00Z"));
        string body = """{"values":[{"path":"navigation.position","method":"first"}],"data":[["2026-04-25T12:00:00Z",[-76.0,24.0]],["2026-04-25T12:00:30Z",[-76.0,24.01]]]}""";
        var (api, cache, getCount) = CachedHistoryApi(body, time);
        var window = (
            from: new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero),
            to: new DateTimeOffset(2026, 4, 25, 12, 15, 50, TimeSpan.Zero));

        await api.GetServerTrackPointsAsync(
            from: window.from, to: window.to, timespan: null, resolution: "1s",
            bbox: new TrackBbox(South: 47.0, West: 8.0, North: 48.0, East: 9.0));
        int afterFirst = getCount();

        await api.GetServerTrackPointsAsync(
            from: window.from, to: window.to, timespan: null, resolution: "1s",
            bbox: new TrackBbox(South: 24.0, West: -77.0, North: 25.0, East: -76.0));

        int afterSecond = getCount();
        await Assert.That(afterSecond - afterFirst).IsGreaterThan(0);
    }

    [Test]
    public async Task Cache_LiveTailChunk_RefetchesEveryTime()
    {
        // Live-tail rule: chunks whose `to` is within HeadFreshness
        // (60 s) of "now" must NOT be cached and must NOT be served
        // from cache. If the boat is currently underway, the head
        // chunk is still being filled; a cached "you're stationary"
        // entry would be stale. Pin both directions: cache stays
        // empty after the first load, and the second load hits the
        // server the same number of times.
        //
        // Setup math: 510 s window @ 1 s resolution. Chunking
        // triggers (>500 expected rows). Chunk size = 475 s
        // (= 500 × 0.95 × 1 s):
        //   chunk 1: [t, t+475]   -> to = t + 475 s
        //   chunk 2: [t+475, t+510] -> to = t + 510 s
        // Set "now" = t + 510 s (= window end). Live-tail boundary
        // = now - 60 s = t + 450 s. Both chunk `to`s (475 and 510)
        // are AFTER 450, so both are live-tail.
        var windowStart = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);
        var windowEnd = windowStart.AddSeconds(510);
        var time = new FrozenTime(windowEnd);
        string body = """{"values":[{"path":"navigation.position","method":"first"}],"data":[["2026-04-25T12:00:00Z",[-76.0,24.0]],["2026-04-25T12:00:30Z",[-76.0,24.01]]]}""";
        var (api, cache, getCount) = CachedHistoryApi(body, time);

        await api.GetServerTrackPointsAsync(
            from: windowStart, to: windowEnd, timespan: null, resolution: "1s");
        int afterFirst = getCount();
        await Assert.That(afterFirst)
            .IsEqualTo(2)
            .Because("510 s @ 1 s exceeds the 500-row cap -> chunked into 2 sub-requests");

        // Cache must be EMPTY after the first load: every chunk
        // is in the tail and HistoryCache.Set silently no-ops.
        await Assert.That(cache.Count).IsEqualTo(0);

        // Second identical load: cache stays empty, server is hit
        // again the same number of times.
        await api.GetServerTrackPointsAsync(
            from: windowStart, to: windowEnd, timespan: null, resolution: "1s");

        int afterSecond = getCount();
        await Assert.That(afterSecond)
            .IsEqualTo(afterFirst * 2)
            .Because("live-tail chunks must refetch unconditionally; " +
                     "caching them would surface stale 'boat hasn't moved' data");
        await Assert.That(cache.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Cache_SmallSingleRequestWindow_DoesNotCache()
    {
        // Below-cap windows take the original single-request path
        // (`duration=PT1H`, no chunking). That path bypasses the
        // cache entirely because the relative form would always
        // miss against absolute-bound cache keys. Pin the
        // bypass: cache stays empty even after a small fetch.
        var time = new FrozenTime(DateTimeOffset.Parse("2030-01-01T00:00:00Z"));
        string body = """{"values":[{"path":"navigation.position","method":"first"}],"data":[["2026-04-25T12:00:00Z",[-76.0,24.0]]]}""";
        var (api, cache, _) = CachedHistoryApi(body, time);

        await api.GetServerTrackPointsAsync(
            from: null, to: null, timespan: "1h", resolution: "30s");

        await Assert.That(cache.Count).IsEqualTo(0);
    }

    [Test]
    public async Task History_BadRequest_Returns_Null()
    {
        // signalk-server returns 400 (not 404) for /history/values when
        // it has a history-route handler registered but the request
        // shape doesn't match the registered OpenAPI spec, or when the
        // provider plugin rejects the params. Same outcome as 404 from
        // the page's perspective: TrackApi returns null and the History
        // page shows the "no track data" hint instead of a stale map.
        // Pinned because the CI dev SignalK image hits this path on
        // every page load and the smoke test relies on the empty-state
        // fallback (no exception, no Blazor crash UI).
        var http = ApiTestHelpers.MockClient(_ =>
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":"history provider not configured"}""",
                    System.Text.Encoding.UTF8, "application/json"),
            });
        var api = new TrackApi(http, ApiTestHelpers.FixedBaseUrl());
        var pts = await api.GetServerTrackAsync("1h");
        await Assert.That(pts).IsNull();
    }
}
