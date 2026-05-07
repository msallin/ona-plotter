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
        // heading, TWS, TWA - all from the per-row column indices the
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
        // s,w,n,e. Pin the formatted values as substrings - a future
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
    // Single-request contract: every (from, to, timespan, resolution,
    // bbox) shape lands on exactly one HTTP request. The client used to
    // chunk large windows to dodge a 500-row response cap that older
    // signalk-parquet builds enforced; helm field-tested that current
    // providers honour the requested resolution and the chunking added
    // latency for no benefit. Page-level CancellationToken (Cancel
    // button + 30 s timeout) handles the slow-server case.
    // -----------------------------------------------------------------

    [Test]
    public async Task RichFetch_AnyWindow_StaysSingleRequest()
    {
        // Both the small-window (fits-anywhere) and the previously-
        // chunked large-window (7 d at 30 s) shapes must each land on
        // exactly one HTTP request. Replaces the earlier paginating
        // tests; a regression that re-introduces chunking goes red here.
        int absoluteSmall = 0, relativeSmall = 0, absoluteLarge = 0, relativeLarge = 0;
        string body = """{"values":[{"path":"navigation.position","method":"first"}],"data":[["2026-04-23T14:00:00Z",[-76,24]]]}""";
        var api1 = HistoryApi(body, _ => absoluteSmall++);
        var api2 = HistoryApi(body, _ => relativeSmall++);
        var api3 = HistoryApi(body, _ => absoluteLarge++);
        var api4 = HistoryApi(body, _ => relativeLarge++);

        await api1.GetServerTrackPointsAsync(
            from: new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero),
            to: new DateTimeOffset(2026, 4, 1, 1, 0, 0, TimeSpan.Zero),
            timespan: null);
        await api2.GetServerTrackPointsAsync(from: null, to: null, timespan: "1h");
        await api3.GetServerTrackPointsAsync(
            from: new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero),
            to: new DateTimeOffset(2026, 4, 8, 0, 0, 0, TimeSpan.Zero),
            timespan: null,
            resolution: "30s");
        await api4.GetServerTrackPointsAsync(from: null, to: null, timespan: "7d", resolution: "30s");

        await Assert.That(absoluteSmall).IsEqualTo(1);
        await Assert.That(relativeSmall).IsEqualTo(1);
        await Assert.That(absoluteLarge).IsEqualTo(1);
        await Assert.That(relativeLarge).IsEqualTo(1);
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
