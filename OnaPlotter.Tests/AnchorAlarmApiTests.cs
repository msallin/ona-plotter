using System.Net;
using System.Text.Json;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Tests;

/// <summary>
/// Wire-shape contract for <see cref="AnchorAlarmApi"/>. Pin the URL +
/// HTTP method + JSON body shape the plugin actually expects so a
/// future rename or arg-shape drift breaks the test, not the helm
/// trying to drop anchor at sundown. Body assertions parse the JSON
/// rather than substring-matching so a refactor that ships
/// <c>{"radiusFoo":"abc25xyz"}</c> -- which would pass <c>Contains</c>
/// checks -- fails here instead of in production.
/// </summary>
public class AnchorAlarmApiTests
{
    private static AnchorAlarmApi NewApi(HttpClient client) =>
        new(client, ApiTestHelpers.FixedBaseUrl());

    private sealed record CapturedRequest(string Method, string Url, string Body);

    private static (HttpClient client, List<CapturedRequest> log)
        CapturingClient(HttpStatusCode status = HttpStatusCode.OK, string responseBody = "{}")
    {
        var log = new List<CapturedRequest>();
        var client = ApiTestHelpers.MockClient(req =>
        {
            string body = req.Content?.ReadAsStringAsync().Result ?? "";
            log.Add(new CapturedRequest(req.Method.Method, req.RequestUri!.ToString(), body));
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, System.Text.Encoding.UTF8, "application/json"),
            };
        });
        return (client, log);
    }

    /// <summary>Throwing client used to exercise the network-error
    /// branch of ResourceHttp.PostAsync. The same handler is invoked
    /// once per call; each call surfaces the exception as a non-
    /// success ApiResult so the caller can branch on r.Success.</summary>
    private static HttpClient ThrowingClient(Exception toThrow) =>
        ApiTestHelpers.MockClient(_ => throw toThrow);

    [Test]
    public async Task DropAsync_Posts_To_DropAnchor_Path_With_Radius_Body()
    {
        // Pinning the wire shape: POST {base}/plugins/anchoralarm/dropAnchor
        // with body {"radius": <integer>}. Parse the body as JSON so a
        // refactor that ships {"radiusFoo":"abc25xyz"} -- which would
        // pass a Contains("radius") + Contains("25") substring check --
        // fails here instead of 400'ing against the live plugin.
        var (client, log) = CapturingClient();
        var api = NewApi(client);

        var r = await api.DropAsync(25);

        await Assert.That(r.Success).IsTrue();
        await Assert.That(log.Count).IsEqualTo(1);
        await Assert.That(log[0].Method).IsEqualTo("POST");
        await Assert.That(log[0].Url).IsEqualTo(ApiTestHelpers.TestBase + "/plugins/anchoralarm/dropAnchor");

        using var doc = JsonDocument.Parse(log[0].Body);
        await Assert.That(doc.RootElement.ValueKind).IsEqualTo(JsonValueKind.Object);
        await Assert.That(doc.RootElement.TryGetProperty("radius", out var radius)).IsTrue();
        await Assert.That(radius.ValueKind).IsEqualTo(JsonValueKind.Number);
        await Assert.That(radius.GetInt32()).IsEqualTo(25);
    }

    [Test]
    public async Task DropAsync_404_Returns_Failure()
    {
        // Plugin not installed = 404 from the SK server's plugin router.
        // Caller (Map.razor's drop flow) needs r.Success=false to fall
        // back to the JS-only manual flow rather than silently no-op.
        var (client, _) = CapturingClient(status: HttpStatusCode.NotFound, responseBody: "");
        var api = NewApi(client);

        var r = await api.DropAsync(30);

        await Assert.That(r.Success).IsFalse();
    }

    [Test]
    public async Task DropAsync_500_Returns_Failure()
    {
        // 5xx covers "plugin installed but threw" -- a config bug in
        // the plugin, a missing depth source for rode-counter mode,
        // an exception inside the plugin's drop handler. Same caller
        // contract as 404: r.Success=false, surface in toast.
        var (client, _) = CapturingClient(
            status: HttpStatusCode.InternalServerError,
            responseBody: "{\"error\":\"depth source unavailable\"}");
        var api = NewApi(client);

        var r = await api.DropAsync(30);

        await Assert.That(r.Success).IsFalse();
    }

    [Test]
    public async Task DropAsync_NetworkException_Returns_Failure_With_Message()
    {
        // SK server unreachable (helm just power-cycled the Pi, helm
        // off the boat's wifi, ...). The HttpClient throws; the API
        // wraps it into an ApiResult.Fail with the exception message
        // so the caller can surface "what went wrong" in the toast
        // instead of a generic "drop failed".
        var client = ThrowingClient(new HttpRequestException("connection refused"));
        var api = NewApi(client);

        var r = await api.DropAsync(30);

        await Assert.That(r.Success).IsFalse();
        await Assert.That(r.Error).IsNotNull();
        await Assert.That(r.Error!).Contains("connection refused");
    }

    [Test]
    public async Task RaiseAsync_Posts_To_RaiseAnchor_Path()
    {
        // Body must still be JSON object (plugin requires Content-Type
        // application/json) but is otherwise empty per docs. Pin the
        // shape via a JSON parse rather than substring so a refactor
        // that ships {} vs {"raise":true} is detectable.
        var (client, log) = CapturingClient();
        var api = NewApi(client);

        var r = await api.RaiseAsync();

        await Assert.That(r.Success).IsTrue();
        await Assert.That(log[0].Method).IsEqualTo("POST");
        await Assert.That(log[0].Url).IsEqualTo(ApiTestHelpers.TestBase + "/plugins/anchoralarm/raiseAnchor");

        using var doc = JsonDocument.Parse(log[0].Body);
        await Assert.That(doc.RootElement.ValueKind).IsEqualTo(JsonValueKind.Object);
        // Object should have zero properties (the spec body is empty
        // {}); pin that so a future change adding {"foo":1} surfaces.
        int propCount = 0;
        foreach (var _ in doc.RootElement.EnumerateObject()) propCount++;
        await Assert.That(propCount).IsEqualTo(0);
    }
}
