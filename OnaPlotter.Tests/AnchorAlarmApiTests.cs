using System.Net;
using System.Text.Json;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Tests;

/// <summary>
/// Wire-shape contract for the v2.0.0+ <see cref="AnchorAlarmApi"/>.
/// Pin URL + HTTP method + JSON body for every method so a future
/// rename or arg-shape drift breaks the test, not the helm trying to
/// drop anchor at sundown. Body assertions parse the JSON rather than
/// substring-matching so a refactor that ships
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

    private static HttpClient ThrowingClient(Exception toThrow) =>
        ApiTestHelpers.MockClient(_ => throw toThrow);

    // ---- DropAtCurrentPositionAsync (PUT navigation.anchor.position) ----

    [Test]
    public async Task DropAtCurrentPosition_PUTs_AnchorPositionPath_With_Position_Object()
    {
        // Pin the wire shape: PUT {base}/signalk/v1/api/vessels/self/navigation/anchor/position
        // body {"value": {"latitude": ..., "longitude": ..., "altitude": ...}}.
        // Altitude is the depth at the drop point, NEGATED (SK convention:
        // below water = negative metres). Float-equality tolerant compare
        // because System.Text.Json may round-trip 47.5 -> 47.499999...
        var (client, log) = CapturingClient();
        var api = NewApi(client);

        var r = await api.DropAtCurrentPositionAsync(47.5, 8.7, depthMeters: 6.4);

        await Assert.That(r.Success).IsTrue();
        await Assert.That(log.Count).IsEqualTo(1);
        await Assert.That(log[0].Method).IsEqualTo("PUT");
        await Assert.That(log[0].Url)
            .IsEqualTo(ApiTestHelpers.TestBase + "/signalk/v1/api/vessels/self/navigation/anchor/position");

        using var doc = JsonDocument.Parse(log[0].Body);
        await Assert.That(doc.RootElement.TryGetProperty("value", out var value)).IsTrue();
        await Assert.That(value.ValueKind).IsEqualTo(JsonValueKind.Object);
        await Assert.That(value.GetProperty("latitude").GetDouble()).IsEqualTo(47.5);
        await Assert.That(value.GetProperty("longitude").GetDouble()).IsEqualTo(8.7);
        await Assert.That(value.GetProperty("altitude").GetDouble()).IsEqualTo(-6.4);
    }

    [Test]
    public async Task DropAtCurrentPosition_NegativeDepth_StillEmitsNegative()
    {
        // Defensive: if a caller hands us depth as -6.4 (already negative),
        // we still negate the absolute value so the wire is unambiguously
        // "below water = negative metres". Otherwise -(-6.4) = +6.4 and
        // the plugin would record the anchor 6.4 m above water level.
        var (client, log) = CapturingClient();
        var api = NewApi(client);

        await api.DropAtCurrentPositionAsync(47.5, 8.7, depthMeters: -6.4);

        using var doc = JsonDocument.Parse(log[0].Body);
        await Assert.That(doc.RootElement.GetProperty("value")
            .GetProperty("altitude").GetDouble()).IsEqualTo(-6.4);
    }

    [Test]
    public async Task DropAtCurrentPosition_NullDepth_OmitsAltitudeKey()
    {
        // No depth source published -> we send {latitude, longitude}
        // without an altitude key. Plugin tolerates the missing field
        // and derives altitude from environment.depth.belowSurface
        // when that's available. Pinning the omission here so a
        // refactor that always writes "altitude": 0 doesn't silently
        // record every anchor at sea level.
        var (client, log) = CapturingClient();
        var api = NewApi(client);

        await api.DropAtCurrentPositionAsync(47.5, 8.7, depthMeters: null);

        using var doc = JsonDocument.Parse(log[0].Body);
        var value = doc.RootElement.GetProperty("value");
        await Assert.That(value.TryGetProperty("altitude", out _)).IsFalse();
        await Assert.That(value.GetProperty("latitude").GetDouble()).IsEqualTo(47.5);
        await Assert.That(value.GetProperty("longitude").GetDouble()).IsEqualTo(8.7);
    }

    [Test]
    public async Task DropAtCurrentPosition_405_Returns_Failure()
    {
        // 405 = plugin v1.x is installed (it has POST /dropAnchor but
        // not the PUT handler). Per the locked-in fail-loud decision
        // the caller toasts "v2.0.0+ required"; no probe-and-fallback.
        var (client, _) = CapturingClient(status: HttpStatusCode.MethodNotAllowed);
        var api = NewApi(client);

        var r = await api.DropAtCurrentPositionAsync(47.5, 8.7, 6.4);

        await Assert.That(r.Success).IsFalse();
    }

    [Test]
    public async Task DropAtCurrentPosition_NetworkException_Returns_Failure_With_Message()
    {
        var client = ThrowingClient(new HttpRequestException("connection refused"));
        var api = NewApi(client);

        var r = await api.DropAtCurrentPositionAsync(47.5, 8.7, 6.4);

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
    }

    // ---- AutoSetRadiusAsync (POST /plugins/anchoralarm/setRadius) ----

    [Test]
    public async Task AutoSetRadius_POSTs_PluginSetRadiusPath_With_EmptyBody()
    {
        // Pin: POST {base}/plugins/anchoralarm/setRadius with body {}
        // (Content-Type: application/json -- empty string would 415).
        // Plugin uses the empty body as "compute the radius from
        // current distance to drop point".
        var (client, log) = CapturingClient();
        var api = NewApi(client);

        var r = await api.AutoSetRadiusAsync();

        await Assert.That(r.Success).IsTrue();
        await Assert.That(log[0].Method).IsEqualTo("POST");
        await Assert.That(log[0].Url)
            .IsEqualTo(ApiTestHelpers.TestBase + "/plugins/anchoralarm/setRadius");

        using var doc = JsonDocument.Parse(log[0].Body);
        await Assert.That(doc.RootElement.ValueKind).IsEqualTo(JsonValueKind.Object);
        int propCount = 0;
        foreach (var _ in doc.RootElement.EnumerateObject()) propCount++;
        await Assert.That(propCount).IsEqualTo(0);
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
    }
}
