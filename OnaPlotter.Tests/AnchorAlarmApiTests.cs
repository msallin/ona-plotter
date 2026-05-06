using System.Net;
using System.Text.Json;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Tests;

/// <summary>
/// Wire-shape contract for the v2.0.0+ <see cref="AnchorAlarmApi"/>.
/// Pin URL + HTTP method + JSON body for every method so a future
/// rename or arg-shape drift breaks the test, not the helm trying
/// to drop anchor at sundown. Body assertions parse the JSON rather
/// than substring-matching.
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

    private static HttpClient ThrowingClient(Exception toThrow) =>
        ApiTestHelpers.MockClient(_ => throw toThrow);

    // ---- DropAsync (POST /plugins/anchoralarm/dropAnchor) ----
    // Plugin reads current GPS internally; client doesn't ship lat/lon
    // (helm preferred this so the wire stays close to the plugin's
    // admin UI behaviour). Empty JSON body; maxRadius stays null
    // until step 2 lands.

    [Test]
    public async Task DropAsync_POSTs_PluginDropAnchorPath_With_EmptyBody()
    {
        var (client, log) = CapturingClient();
        var api = NewApi(client);

        var r = await api.DropAsync();

        await Assert.That(r.Success).IsTrue();
        await Assert.That(log.Count).IsEqualTo(1);
        await Assert.That(log[0].Method).IsEqualTo("POST");
        await Assert.That(log[0].Url)
            .IsEqualTo(ApiTestHelpers.TestBase + "/plugins/anchoralarm/dropAnchor");

        using var doc = JsonDocument.Parse(log[0].Body);
        await Assert.That(doc.RootElement.ValueKind).IsEqualTo(JsonValueKind.Object);
        int propCount = 0;
        foreach (var _ in doc.RootElement.EnumerateObject()) propCount++;
        await Assert.That(propCount).IsEqualTo(0);
    }

    [Test]
    public async Task DropAsync_404_Returns_Failure_With_StatusCode()
    {
        // Plugin not installed -- /plugins/anchoralarm prefix absent.
        // Caller toast routes to "v2.0.0+ required".
        var (client, _) = CapturingClient(status: HttpStatusCode.NotFound);
        var api = NewApi(client);

        var r = await api.DropAsync();

        await Assert.That(r.Success).IsFalse();
        await Assert.That(r.StatusCode).IsEqualTo(404);
    }

    [Test]
    public async Task DropAsync_403_Returns_Failure_With_StatusCode()
    {
        // Auth misconfig -- read-only role can't write. Caller toast
        // routes to "permission denied" via StatusCode disambiguation.
        var (client, _) = CapturingClient(status: HttpStatusCode.Forbidden);
        var api = NewApi(client);

        var r = await api.DropAsync();

        await Assert.That(r.Success).IsFalse();
        await Assert.That(r.StatusCode).IsEqualTo(403);
    }

    [Test]
    public async Task DropAsync_NetworkException_Returns_Failure()
    {
        var client = ThrowingClient(new HttpRequestException("connection refused"));
        var api = NewApi(client);

        var r = await api.DropAsync();

        await Assert.That(r.Success).IsFalse();
        await Assert.That(r.Error).IsNotNull();
        await Assert.That(r.Error!).Contains("connection refused");
    }

    // ---- SetMaxRadiusAsync (PUT navigation.anchor.maxRadius) ----

    [Test]
    public async Task SetMaxRadius_PUTs_AnchorMaxRadiusPath_With_Value_Number()
    {
        var (client, log) = CapturingClient();
        var api = NewApi(client);

        var r = await api.SetMaxRadiusAsync(50);

        await Assert.That(r.Success).IsTrue();
        await Assert.That(log.Count).IsEqualTo(1);
        await Assert.That(log[0].Method).IsEqualTo("PUT");
        await Assert.That(log[0].Url)
            .IsEqualTo(ApiTestHelpers.TestBase + "/signalk/v1/api/vessels/self/navigation/anchor/maxRadius");

        using var doc = JsonDocument.Parse(log[0].Body);
        await Assert.That(doc.RootElement.TryGetProperty("value", out var value)).IsTrue();
        await Assert.That(value.ValueKind).IsEqualTo(JsonValueKind.Number);
        await Assert.That(value.GetInt32()).IsEqualTo(50);
    }

    [Test]
    public async Task SetMaxRadius_500_Returns_Failure()
    {
        // Plugin installed but threw (config bug, missing fudge factor,
        // exception inside the handler). r.Success=false; caller
        // toasts the server error envelope.
        var (client, _) = CapturingClient(
            status: HttpStatusCode.InternalServerError,
            responseBody: "{\"error\":\"depth source unavailable\"}");
        var api = NewApi(client);

        var r = await api.SetMaxRadiusAsync(50);

        await Assert.That(r.Success).IsFalse();
        await Assert.That(r.Error).IsNotNull();
        await Assert.That(r.Error!).Contains("depth source unavailable");
        await Assert.That(r.StatusCode).IsEqualTo(500);
    }

    [Test]
    public async Task SetMaxRadius_Zero_EmitsZeroNotOmitted()
    {
        // Boundary: radius of 0 must emit `"value": 0`, not omit the
        // key entirely. A future refactor that swaps to
        // DefaultIgnoreCondition.WhenWritingDefault would silently
        // drop the property -- caught here.
        var (client, log) = CapturingClient();
        var api = NewApi(client);

        await api.SetMaxRadiusAsync(0);

        using var doc = JsonDocument.Parse(log[0].Body);
        await Assert.That(doc.RootElement.GetProperty("value").ValueKind)
            .IsEqualTo(JsonValueKind.Number);
        await Assert.That(doc.RootElement.GetProperty("value").GetInt32()).IsEqualTo(0);
    }

    [Test]
    public async Task SetMaxRadius_IntMaxValue_RoundTrips()
    {
        var (client, log) = CapturingClient();
        var api = NewApi(client);

        await api.SetMaxRadiusAsync(int.MaxValue);

        using var doc = JsonDocument.Parse(log[0].Body);
        await Assert.That(doc.RootElement.GetProperty("value").GetInt32()).IsEqualTo(int.MaxValue);
    }

    [Test]
    public async Task SetMaxRadius_Negative_ShipsLiteralValue()
    {
        // Pin the policy: negative radius ships through as-is
        // (rejected by plugin server-side), not silently clamped to
        // 0. Forces the helm-facing error to the right layer (the
        // plugin's response, not a silent client-side normalisation).
        var (client, log) = CapturingClient();
        var api = NewApi(client);

        await api.SetMaxRadiusAsync(-30);

        using var doc = JsonDocument.Parse(log[0].Body);
        await Assert.That(doc.RootElement.GetProperty("value").GetInt32()).IsEqualTo(-30);
    }

    // ---- RaiseAsync (PUT navigation.anchor.position with null) ----

    [Test]
    public async Task RaiseAsync_PUTs_AnchorPositionPath_With_Null_Value()
    {
        // SK-spec way to clear an anchor: PUT navigation.anchor.position
        // with {value: null}. Plugin handler treats null as raise.
        // Pin the literal JSON null (not "null" string) so a refactor
        // that omits the value key fails here.
        var (client, log) = CapturingClient();
        var api = NewApi(client);

        var r = await api.RaiseAsync();

        await Assert.That(r.Success).IsTrue();
        await Assert.That(log[0].Method).IsEqualTo("PUT");
        await Assert.That(log[0].Url)
            .IsEqualTo(ApiTestHelpers.TestBase + "/signalk/v1/api/vessels/self/navigation/anchor/position");

        using var doc = JsonDocument.Parse(log[0].Body);
        await Assert.That(doc.RootElement.TryGetProperty("value", out var value)).IsTrue();
        await Assert.That(value.ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task RaiseAsync_405_Returns_Failure()
    {
        var (client, _) = CapturingClient(status: HttpStatusCode.MethodNotAllowed);
        var api = NewApi(client);

        var r = await api.RaiseAsync();

        await Assert.That(r.Success).IsFalse();
        await Assert.That(r.StatusCode).IsEqualTo(405);
    }
}
