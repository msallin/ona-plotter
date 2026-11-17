using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OnaPlotter.Models;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the shared failure policy in <see cref="ResourceHttp"/>
/// and the API clients that go through it. Every read and write must turn a
/// non-2xx, a transport failure, an HttpClient timeout or a malformed
/// body into a failed result (never an exception into Blazor's
/// renderer); only a caller-initiated cancel may propagate.
/// </summary>
public class ResourceHttpTests
{
    private const string Url = ApiTestHelpers.TestBase + "/x";

    private sealed record Dto(string? Name, int Count);

    /// <summary>Simulates HttpClient's internal timeout: the handler
    /// throws a TaskCanceledException although the caller's token was
    /// never cancelled.</summary>
    private static HttpClient TimeoutClient() =>
        ApiTestHelpers.MockClient(_ => throw new TaskCanceledException("timeout"));

    private static HttpClient NetworkDownClient() =>
        ApiTestHelpers.MockClient(_ => throw new HttpRequestException("DNS fail"));

    private static HttpClient MalformedClient() =>
        ApiTestHelpers.JsonClient(_ => "{ not json");

    // --- GetTextAsync -----------------------------------------------

    [Test]
    public async Task GetText_2xx_ReturnsRawBody_EvenWhenNotJson()
    {
        // /signalk/v1/api/self on some servers: a bare, unquoted URN.
        var http = ApiTestHelpers.JsonClient(_ => "urn:mrn:imo:mmsi:261006533");

        var r = await ResourceHttp.GetTextAsync(http, Url, NullLogger.Instance);

        await Assert.That(r.Success).IsTrue();
        await Assert.That(r.Value).IsEqualTo("urn:mrn:imo:mmsi:261006533");
    }

    [Test]
    public async Task GetText_404_FailsWithStatus()
    {
        var http = ApiTestHelpers.JsonClient(_ => "", HttpStatusCode.NotFound);

        var r = await ResourceHttp.GetTextAsync(http, Url, NullLogger.Instance);

        await Assert.That(r.Success).IsFalse();
        await Assert.That(r.StatusCode).IsEqualTo(404);
    }

    [Test]
    public async Task GetText_TransportFailure_FailsAndLogsWarning()
    {
        var logger = new RecordingLogger<ResourceHttpTests>();

        var r = await ResourceHttp.GetTextAsync(NetworkDownClient(), Url, logger);

        await Assert.That(r.Success).IsFalse();
        await Assert.That(r.StatusCode).IsNull();
        await Assert.That(logger.Entries.Single().Level)
            .IsEqualTo(Microsoft.Extensions.Logging.LogLevel.Warning);
    }

    [Test]
    public async Task GetText_Timeout_FailsWithoutThrowing()
    {
        var r = await ResourceHttp.GetTextAsync(TimeoutClient(), Url, NullLogger.Instance);

        await Assert.That(r.Success).IsFalse();
        await Assert.That(r.Error).IsEqualTo("request timed out");
    }

    // --- GetJsonAsync<T> -------------------------------------------

    [Test]
    public async Task GetJson_2xx_DeserialisesCaseInsensitively()
    {
        var http = ApiTestHelpers.JsonClient(_ => """{"name":"a","count":3}""");

        var r = await ResourceHttp.GetJsonAsync<Dto>(http, Url, NullLogger.Instance);

        await Assert.That(r.Success).IsTrue();
        await Assert.That(r.Value).IsEqualTo(new Dto("a", 3));
        await Assert.That(r.StatusCode).IsNull();
    }

    [Test]
    public async Task GetJson_NullBody_IsSuccessWithNullValue()
    {
        var http = ApiTestHelpers.JsonClient(_ => "null");

        var r = await ResourceHttp.GetJsonAsync<Dto>(http, Url, NullLogger.Instance);

        await Assert.That(r.Success).IsTrue();
        await Assert.That(r.Value).IsNull();
    }

    [Test]
    [Arguments(HttpStatusCode.NotFound)]
    [Arguments(HttpStatusCode.Unauthorized)]
    [Arguments(HttpStatusCode.InternalServerError)]
    public async Task GetJson_Non2xx_FailsWithStatusCode(HttpStatusCode status)
    {
        var http = ApiTestHelpers.MockClient(_ => new HttpResponseMessage(status));

        var r = await ResourceHttp.GetJsonAsync<Dto>(http, Url, NullLogger.Instance);

        await Assert.That(r.Success).IsFalse();
        await Assert.That(r.StatusCode).IsEqualTo((int)status);
        await Assert.That(r.Error).IsEqualTo($"HTTP {(int)status}");
    }

    [Test]
    public async Task GetJson_Non2xx_SurfacesServerErrorEnvelope()
    {
        var http = ApiTestHelpers.JsonClient(
            _ => """{"state":"FAILED","statusCode":400,"message":"bad bbox"}""",
            HttpStatusCode.BadRequest);

        var r = await ResourceHttp.GetJsonAsync<Dto>(http, Url, NullLogger.Instance);

        await Assert.That(r.Error).IsEqualTo("bad bbox");
        await Assert.That(r.StatusCode).IsEqualTo(400);
    }

    [Test]
    public async Task GetJson_NetworkFailure_FailsWithoutStatus()
    {
        var r = await ResourceHttp.GetJsonAsync<Dto>(NetworkDownClient(), Url, NullLogger.Instance);

        await Assert.That(r.Success).IsFalse();
        await Assert.That(r.StatusCode).IsNull();
        await Assert.That(r.Error).IsEqualTo("DNS fail");
    }

    [Test]
    public async Task GetJson_HttpClientTimeout_Fails()
    {
        var r = await ResourceHttp.GetJsonAsync<Dto>(TimeoutClient(), Url, NullLogger.Instance);

        await Assert.That(r.Success).IsFalse();
        await Assert.That(r.Error).IsEqualTo("request timed out");
    }

    [Test]
    public async Task GetJson_MalformedBody_Fails()
    {
        var r = await ResourceHttp.GetJsonAsync<Dto>(MalformedClient(), Url, NullLogger.Instance);

        await Assert.That(r.Success).IsFalse();
        await Assert.That(r.Error).IsEqualTo("malformed response");
    }

    [Test]
    public async Task GetJson_CallerCancel_Propagates()
    {
        using var cts = new CancellationTokenSource();
        var http = ApiTestHelpers.MockClient(_ =>
        {
            cts.Cancel();
            throw new TaskCanceledException();
        });

        await Assert.That(async () =>
                await ResourceHttp.GetJsonAsync<Dto>(http, Url, NullLogger.Instance, cts.Token))
            .Throws<OperationCanceledException>();
    }

    // --- GetJsonAsync with a parse callback -------------------------

    [Test]
    public async Task GetJsonParse_RunsParserOnRoot()
    {
        var http = ApiTestHelpers.JsonClient(_ => "[1,2,3]");

        var r = await ResourceHttp.GetJsonAsync(http, Url, NullLogger.Instance,
            static root => root.GetArrayLength());

        await Assert.That(r.Success).IsTrue();
        await Assert.That(r.Value).IsEqualTo(3);
    }

    [Test]
    public async Task GetJsonParse_ParserJsonException_IsMalformed()
    {
        var http = ApiTestHelpers.JsonClient(_ => "{}");

        var r = await ResourceHttp.GetJsonAsync<int>(http, Url, NullLogger.Instance,
            static _ => throw new JsonException("unexpected shape"));

        await Assert.That(r.Success).IsFalse();
        await Assert.That(r.Error).IsEqualTo("malformed response");
    }

    [Test]
    public async Task GetJsonParse_EmptyBody_IsMalformed()
    {
        var http = ApiTestHelpers.JsonClient(_ => "");

        var r = await ResourceHttp.GetJsonAsync(http, Url, NullLogger.Instance,
            static root => root.ValueKind);

        await Assert.That(r.Success).IsFalse();
    }

    // --- GetDictAsync ----------------------------------------------

    [Test]
    public async Task GetDict_AnyFailure_ReturnsNull()
    {
        await Assert.That(await ResourceHttp.GetDictAsync<Dto>(NetworkDownClient(), Url, NullLogger.Instance)).IsNull();
        await Assert.That(await ResourceHttp.GetDictAsync<Dto>(TimeoutClient(), Url, NullLogger.Instance)).IsNull();
        await Assert.That(await ResourceHttp.GetDictAsync<Dto>(MalformedClient(), Url, NullLogger.Instance)).IsNull();
        var notFound = ApiTestHelpers.MockClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        await Assert.That(await ResourceHttp.GetDictAsync<Dto>(notFound, Url, NullLogger.Instance)).IsNull();
    }

    // --- Clients that read through the policy ----------------------

    [Test]
    public async Task ChartApi_TransportTimeoutOrMalformed_FallsBackToBuiltIns()
    {
        // The helm must keep the built-in basemaps whatever the SK
        // server does; a timeout or a garbage body must not escape.
        foreach (var http in new[] { NetworkDownClient(), TimeoutClient(), MalformedClient() })
        {
            var api = new ChartApi(http, ApiTestHelpers.FixedBaseUrl(), NullLogger<ChartApi>.Instance);
            var charts = await api.GetAllAsync();
            await Assert.That(charts.Count).IsEqualTo(OnaPlotter.Utilities.BuiltInCharts.All.Count);
        }
    }

    [Test]
    public async Task ChartApi_ServerError_FallsBackToBuiltIns()
    {
        var http = ApiTestHelpers.MockClient(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        var api = new ChartApi(http, ApiTestHelpers.FixedBaseUrl(), NullLogger<ChartApi>.Instance);

        var charts = await api.GetAllAsync();

        await Assert.That(charts.Count).IsEqualTo(OnaPlotter.Utilities.BuiltInCharts.All.Count);
    }

    [Test]
    public async Task RouteApi_GetCoordinates_TransportTimeoutOrMalformed_ReturnsNull()
    {
        foreach (var http in new[] { NetworkDownClient(), TimeoutClient(), MalformedClient() })
        {
            var api = new RouteApi(http, ApiTestHelpers.FixedBaseUrl(), NullLogger<RouteApi>.Instance);
            await Assert.That(await api.GetCoordinatesAsync("/resources/routes/abc")).IsNull();
        }
    }

    [Test]
    public async Task PathApi_FlattensTree_AndFailsToEmpty()
    {
        var ok = new PathApi(
            ApiTestHelpers.JsonClient(_ => """{"navigation":{"speedOverGround":{"value":3.1}}}"""),
            ApiTestHelpers.FixedBaseUrl(), NullLogger<PathApi>.Instance);
        await Assert.That(await ok.GetAvailablePathsAsync()).Contains("navigation.speedOverGround");

        foreach (var http in new[] { NetworkDownClient(), TimeoutClient(), MalformedClient() })
        {
            var api = new PathApi(http, ApiTestHelpers.FixedBaseUrl(), NullLogger<PathApi>.Instance);
            await Assert.That(await api.GetAvailablePathsAsync()).IsEmpty();
        }
    }

    [Test]
    public async Task TrackApi_TransportTimeoutOrMalformed_ReturnsNull()
    {
        foreach (var http in new[] { NetworkDownClient(), TimeoutClient(), MalformedClient() })
        {
            var api = new TrackApi(http, ApiTestHelpers.FixedBaseUrl(), TimeProvider.System, NullLogger<TrackApi>.Instance);
            await Assert.That(await api.GetServerTrackAsync()).IsNull();
            await Assert.That(await api.GetServerTrackPointsAsync(null, null, "1h")).IsNull();
        }
    }

    [Test]
    public async Task RadarApi_Reads_TransportTimeoutOrMalformed_DegradeToEmpty()
    {
        foreach (var http in new[] { NetworkDownClient(), TimeoutClient(), MalformedClient() })
        {
            var api = new RadarApi(http, ApiTestHelpers.FixedBaseUrl(), NullLogger<RadarApi>.Instance);
            await Assert.That(await api.GetAllAsync()).IsEmpty();
            await Assert.That(await api.GetCapabilitiesAsync("r1")).IsNull();
            await Assert.That(await api.GetControlsAsync("r1")).IsNull();
        }
    }

    [Test]
    public async Task RadarApi_SetControl_PutsWithoutNullFields_AndSurfacesServerError()
    {
        // Mayara rejects bodies carrying "auto":null etc. with HTTP 400,
        // so the PUT body must omit unset optional fields.
        string? body = null;
        HttpMethod? method = null;
        string? url = null;
        var http = ApiTestHelpers.MockClient(req =>
        {
            method = req.Method;
            url = req.RequestUri!.ToString();
            body = req.Content!.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"success":false,"error":"Control range value 12000 is not a legal value","controlId":"range"}"""),
            };
        });
        var api = new RadarApi(http, ApiTestHelpers.FixedBaseUrl(), NullLogger<RadarApi>.Instance);
        var value = new ControlValue { Value = JsonDocument.Parse("12000").RootElement };

        var r = await api.SetControlAsync("r1", "range", value);

        await Assert.That(method).IsEqualTo(HttpMethod.Put);
        await Assert.That(url).IsEqualTo(ApiTestHelpers.TestBase + SignalKUrls.RadarControl("r1", "range"));
        await Assert.That(body).IsEqualTo("""{"value":12000}""");
        await Assert.That(r.Success).IsFalse();
        await Assert.That(r.Error).IsEqualTo("Control range value 12000 is not a legal value");
        await Assert.That(r.StatusCode).IsEqualTo(400);
    }

    [Test]
    public async Task BuddyListApi_TransientFailure_DoesNotCacheNotInstalled()
    {
        // A 503 or a dropped link says nothing about whether the plugin
        // is installed; the next probe must ask the server again.
        int calls = 0;
        var http = ApiTestHelpers.MockClient(_ =>
        {
            calls++;
            return calls == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });
        var api = new BuddyListApi(http, ApiTestHelpers.FixedBaseUrl(), NullLogger<BuddyListApi>.Instance);

        await Assert.That(await api.GetAllAsync()).IsNull();
        await Assert.That(await api.IsAvailableAsync()).IsTrue();
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task BuddyListApi_Add_PostsUrnAndName()
    {
        string? body = null;
        HttpMethod? method = null;
        var http = ApiTestHelpers.MockClient(req =>
        {
            method = req.Method;
            body = req.Content!.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var api = new BuddyListApi(http, ApiTestHelpers.FixedBaseUrl(), NullLogger<BuddyListApi>.Instance);

        var r = await api.AddAsync("urn:mrn:imo:mmsi:338246284", "Morning Star");

        await Assert.That(r.Success).IsTrue();
        await Assert.That(method).IsEqualTo(HttpMethod.Post);
        await Assert.That(body).IsEqualTo("""{"urn":"urn:mrn:imo:mmsi:338246284","name":"Morning Star"}""");
    }

    // --- Write methods share one failure policy ------------------------

    private static Task<ApiResult> Write(string verb, HttpClient http, CancellationToken ct = default) => verb switch
    {
        "DELETE" => ResourceHttp.DeleteAsync(http, Url, ct),
        "POST" => ResourceHttp.PostAsync(http, Url, new { a = 1 }, ct),
        _ => ResourceHttp.PutAsync(http, Url, new { a = 1 }, ct),
    };

    [Test]
    [Arguments("DELETE")]
    [Arguments("POST")]
    [Arguments("PUT")]
    public async Task Write_2xx_IsOk(string verb)
    {
        var http = ApiTestHelpers.MockClient(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        await Assert.That((await Write(verb, http)).Success).IsTrue();
    }

    [Test]
    [Arguments("DELETE")]
    [Arguments("POST")]
    [Arguments("PUT")]
    public async Task Write_Non2xx_FailsWithServerErrorAndStatus(string verb)
    {
        var http = ApiTestHelpers.JsonClient(
            _ => """{"error":"nope"}""", HttpStatusCode.Forbidden);

        var r = await Write(verb, http);

        await Assert.That(r.Success).IsFalse();
        await Assert.That(r.Error).IsEqualTo("nope");
        await Assert.That(r.StatusCode).IsEqualTo(403);
    }

    [Test]
    [Arguments("DELETE")]
    [Arguments("POST")]
    [Arguments("PUT")]
    public async Task Write_NetworkFailureAndTimeout_FailWithoutStatus(string verb)
    {
        var down = await Write(verb, NetworkDownClient());
        var slow = await Write(verb, TimeoutClient());

        await Assert.That(down.Error).IsEqualTo("DNS fail");
        await Assert.That(down.StatusCode).IsNull();
        await Assert.That(slow.Error).IsEqualTo("request timed out");
        await Assert.That(slow.StatusCode).IsNull();
    }

    [Test]
    [Arguments("DELETE")]
    [Arguments("POST")]
    [Arguments("PUT")]
    public async Task Write_CallerCancel_Propagates(string verb)
    {
        using var cts = new CancellationTokenSource();
        var http = ApiTestHelpers.MockClient(_ =>
        {
            cts.Cancel();
            throw new TaskCanceledException("caller cancelled", null, cts.Token);
        });

        await Assert.That(async () => await Write(verb, http, cts.Token)).Throws<OperationCanceledException>();
    }

    // --- PostCreateAsync -------------------------------------------------

    [Test]
    public async Task PostCreate_ReturnsTheCreatedId()
    {
        var http = ApiTestHelpers.JsonClient(
            _ => """{"state":"COMPLETED","statusCode":201,"id":"abc"}""", HttpStatusCode.Created);

        var r = await ResourceHttp.PostCreateAsync(http, Url, new { a = 1 });

        await Assert.That(r.Success).IsTrue();
        await Assert.That(r.Value).IsEqualTo("abc");
    }

    [Test]
    public async Task PostCreate_2xxWithoutId_Fails()
    {
        var http = ApiTestHelpers.MockClient(_ => new HttpResponseMessage(HttpStatusCode.Created));

        var r = await ResourceHttp.PostCreateAsync(http, Url, new { a = 1 });

        await Assert.That(r.Success).IsFalse();
        await Assert.That(r.Error).IsEqualTo("server accepted the create but returned no id");
    }

    [Test]
    public async Task PostCreate_Non2xx_FailsWithServerErrorAndStatus()
    {
        var http = ApiTestHelpers.JsonClient(
            _ => """{"state":"FAILED","statusCode":401,"message":"login required"}""",
            HttpStatusCode.Unauthorized);

        var r = await ResourceHttp.PostCreateAsync(http, Url, new { a = 1 });

        await Assert.That(r.Success).IsFalse();
        await Assert.That(r.Error).IsEqualTo("login required");
        await Assert.That(r.StatusCode).IsEqualTo(401);
    }

    [Test]
    public async Task PostCreate_NetworkFailureAndTimeout_FailWithoutStatus()
    {
        var down = await ResourceHttp.PostCreateAsync(NetworkDownClient(), Url, new { a = 1 });
        var slow = await ResourceHttp.PostCreateAsync(TimeoutClient(), Url, new { a = 1 });

        await Assert.That(down.Error).IsEqualTo("DNS fail");
        await Assert.That(down.StatusCode).IsNull();
        await Assert.That(slow.Error).IsEqualTo("request timed out");
        await Assert.That(slow.StatusCode).IsNull();
    }
}
