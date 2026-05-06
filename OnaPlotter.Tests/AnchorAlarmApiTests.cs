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

    [Test]
    public async Task DropAtCurrentPosition_NaNLat_RejectsLocally_NoHttpCall()
    {
        // Trust-boundary defence: a malformed GPS sensor publishing
        // NaN must not reach PutAsJsonAsync, which would throw a
        // serialiser ArgumentException with an opaque "cannot be NaN"
        // message. Refuse in the API layer with a helm-readable error
        // and don't make the network call.
        var (client, log) = CapturingClient();
        var api = NewApi(client);

        var r = await api.DropAtCurrentPositionAsync(double.NaN, 8.7, 6.4);

        await Assert.That(r.Success).IsFalse();
        await Assert.That(r.Error).IsNotNull();
        await Assert.That(r.Error!).Contains("GPS");
        await Assert.That(log.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DropAtCurrentPosition_InfinityLon_RejectsLocally()
    {
        var (client, log) = CapturingClient();
        var api = NewApi(client);

        var r = await api.DropAtCurrentPositionAsync(47.5, double.PositiveInfinity, 6.4);

        await Assert.That(r.Success).IsFalse();
        await Assert.That(log.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DropAtCurrentPosition_NaNDepth_OmitsAltitudeButShipsLatLon()
    {
        // NaN depth is treated as "no depth source" rather than a
        // hard error -- the helm can still anchor without a depth
        // sensor wired in. Altitude key omitted, lat/lon proceed.
        var (client, log) = CapturingClient();
        var api = NewApi(client);

        var r = await api.DropAtCurrentPositionAsync(47.5, 8.7, double.NaN);

        await Assert.That(r.Success).IsTrue();
        using var doc = JsonDocument.Parse(log[0].Body);
        var value = doc.RootElement.GetProperty("value");
        await Assert.That(value.TryGetProperty("altitude", out _)).IsFalse();
        await Assert.That(value.GetProperty("latitude").GetDouble()).IsEqualTo(47.5);
    }

    [Test]
    public async Task DropAtCurrentPosition_405_StatusCode_Surfaced_For_v1Plugin_Disambiguation()
    {
        // Caller (Map.razor's drop flow) reads StatusCode to
        // distinguish 405 (v1.x plugin -> trigger fallback) from 401
        // (auth -> fail loud with the right hint). Without
        // StatusCode on ApiResult both errors would surface as the
        // same toast.
        var (client, _) = CapturingClient(status: HttpStatusCode.MethodNotAllowed);
        var api = NewApi(client);

        var r = await api.DropAtCurrentPositionAsync(47.5, 8.7, 6.4);

        await Assert.That(r.Success).IsFalse();
        await Assert.That(r.StatusCode).IsEqualTo(405);
    }

    [Test]
    public async Task DropAtCurrentPosition_403_StatusCode_Surfaced_For_AuthDistinction()
    {
        var (client, _) = CapturingClient(status: HttpStatusCode.Forbidden);
        var api = NewApi(client);

        var r = await api.DropAtCurrentPositionAsync(47.5, 8.7, 6.4);

        await Assert.That(r.Success).IsFalse();
        await Assert.That(r.StatusCode).IsEqualTo(403);
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
        // Defensive: a corrupted localStorage value or a hostile
        // caller passing int.MaxValue must not crash the
        // serialiser. Plugin will reject it server-side; we don't
        // clamp client-side, but the wire shape stays well-formed.
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

    [Test]
    public async Task AutoSetRadius_404_Returns_Failure_With_StatusCode()
    {
        // Plugin not installed at all (no /plugins/anchoralarm prefix).
        // Asymmetric vs the PUT-handler endpoints: those return 405
        // when the plugin is at v1.x but the URL exists; this returns
        // 404 because the prefix is absent. Both fail, with different
        // status codes -- caller toast logic uses the code to pick
        // wording.
        var (client, _) = CapturingClient(status: HttpStatusCode.NotFound);
        var api = NewApi(client);

        var r = await api.AutoSetRadiusAsync();

        await Assert.That(r.Success).IsFalse();
        await Assert.That(r.StatusCode).IsEqualTo(404);
    }

    [Test]
    public async Task AutoSetRadius_NetworkException_Returns_Failure()
    {
        var client = ThrowingClient(new HttpRequestException("dns lookup failed"));
        var api = NewApi(client);

        var r = await api.AutoSetRadiusAsync();

        await Assert.That(r.Success).IsFalse();
        await Assert.That(r.Error).IsNotNull();
        await Assert.That(r.Error!).Contains("dns lookup failed");
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
