using System.Net;
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
}
