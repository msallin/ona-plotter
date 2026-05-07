using System.Net;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the wire shape of every verb on <see cref="NotificationsApi"/>.
/// The HTTP client is mocked so each test asserts on the exact method,
/// URL, and body the SignalK v2 server expects - regression guard
/// against a refactor or copy-paste accidentally hitting v1 paths,
/// dropping the JSON body, or mis-encoding an id with special characters.
/// </summary>
public class NotificationsApiTests
{
    [Test]
    public async Task Acknowledge_PostsEmptyBodyToCanonicalUrl()
    {
        // V2 spec: POST /signalk/v2/api/notifications/{id}/acknowledge
        // with an empty JSON object body. The server's content-type
        // check rejects a missing body even on a no-arg action.
        HttpMethod? capturedMethod = null;
        string? capturedUrl = null;
        string? capturedBody = null;
        var http = ApiTestHelpers.MockClient(req =>
        {
            capturedMethod = req.Method;
            capturedUrl = req.RequestUri?.AbsoluteUri;
            capturedBody = req.Content?.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var api = new NotificationsApi(http, ApiTestHelpers.FixedBaseUrl());

        var r = await api.AcknowledgeAsync("550e8400-e29b-41d4-a716-446655440000");

        await Assert.That(r.Success).IsTrue();
        await Assert.That(capturedMethod).IsEqualTo(HttpMethod.Post);
        await Assert.That(capturedUrl).EndsWith(
            "/signalk/v2/api/notifications/550e8400-e29b-41d4-a716-446655440000/acknowledge");
        await Assert.That(capturedBody).IsEqualTo("{}");
    }

    [Test]
    public async Task Silence_PostsToSilenceEndpoint()
    {
        HttpMethod? capturedMethod = null;
        string? capturedUrl = null;
        var http = ApiTestHelpers.MockClient(req =>
        {
            capturedMethod = req.Method;
            capturedUrl = req.RequestUri?.AbsoluteUri;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var api = new NotificationsApi(http, ApiTestHelpers.FixedBaseUrl());

        var r = await api.SilenceAsync("anchor-uuid-1");

        await Assert.That(r.Success).IsTrue();
        await Assert.That(capturedMethod).IsEqualTo(HttpMethod.Post);
        await Assert.That(capturedUrl).EndsWith(
            "/signalk/v2/api/notifications/anchor-uuid-1/silence");
    }

    [Test]
    public async Task Raise_PostsPathAndValueEnvelope()
    {
        // The v2 raise contract is { path, value } where value is the
        // SignalK notification payload (state / method / message). The
        // server derives the id from (context, path, $source), so
        // re-raising the same path overlays the existing entry. This
        // is what makes Phase B (publish client alarms) cross-plotter
        // sync work without the client tracking ids itself.
        string? capturedUrl = null;
        string? capturedBody = null;
        var http = ApiTestHelpers.MockClient(req =>
        {
            capturedUrl = req.RequestUri?.AbsoluteUri;
            capturedBody = req.Content?.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("\"new-id-42\"")
            };
        });
        var api = new NotificationsApi(http, ApiTestHelpers.FixedBaseUrl());

        var body = new NotificationPayload(
            State: "alarm",
            Method: ["visual", "sound"],
            Message: "Wind shifted 30 degrees");
        var r = await api.RaiseAsync("notifications.environment.wind.shift", body);

        await Assert.That(r.Success).IsTrue();
        await Assert.That(r.Value).IsEqualTo("new-id-42");
        await Assert.That(capturedUrl).EndsWith("/signalk/v2/api/notifications");
        await Assert.That(capturedBody).IsNotNull();
        await Assert.That(capturedBody).Contains("\"path\":\"notifications.environment.wind.shift\"");
        await Assert.That(capturedBody).Contains("\"state\":\"alarm\"");
        await Assert.That(capturedBody).Contains("\"message\":\"Wind shifted 30 degrees\"");
        // Method array round-trips as an array, not a comma-string. The
        // server validates with strict typing; a stringified list 400s.
        await Assert.That(capturedBody).Contains("\"method\":[\"visual\",\"sound\"]");
    }

    [Test]
    public async Task Clear_DeletesNotificationById()
    {
        // DELETE clears the notification on the server (state -> normal,
        // 60s GC). Used by the publisher when the local rule that
        // raised a notification stops firing.
        HttpMethod? capturedMethod = null;
        string? capturedUrl = null;
        var http = ApiTestHelpers.MockClient(req =>
        {
            capturedMethod = req.Method;
            capturedUrl = req.RequestUri?.AbsoluteUri;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var api = new NotificationsApi(http, ApiTestHelpers.FixedBaseUrl());

        var r = await api.ClearAsync("anchor-uuid-1");

        await Assert.That(r.Success).IsTrue();
        await Assert.That(capturedMethod).IsEqualTo(HttpMethod.Delete);
        await Assert.That(capturedUrl).EndsWith("/signalk/v2/api/notifications/anchor-uuid-1");
    }

    [Test]
    public async Task Acknowledge_UrlEncodesIdsWithSpecialChars()
    {
        // Server-generated UUIDs are fine, but a rogue plugin could
        // theoretically supply an id containing a slash or space. The
        // URL builder must percent-encode so the path doesn't escape
        // the /notifications/ namespace.
        string? capturedUrl = null;
        var http = ApiTestHelpers.MockClient(req =>
        {
            capturedUrl = req.RequestUri?.AbsoluteUri;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var api = new NotificationsApi(http, ApiTestHelpers.FixedBaseUrl());

        await api.AcknowledgeAsync("id with/space");

        await Assert.That(capturedUrl).EndsWith(
            "/signalk/v2/api/notifications/id%20with%2Fspace/acknowledge");
    }

    [Test]
    public async Task Acknowledge_PropagatesServerFailure()
    {
        // A 404 (pre-2.21 server, or notification GC'd before the ack
        // landed) must surface as Success=false. The dismiss path
        // logs the result; a silent swallow would hide a real bug.
        var http = ApiTestHelpers.MockClient(req =>
            new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("Not Found")
            });
        var api = new NotificationsApi(http, ApiTestHelpers.FixedBaseUrl());

        var r = await api.AcknowledgeAsync("missing");

        await Assert.That(r.Success).IsFalse();
    }

    [Test]
    public async Task Acknowledge_PropagatesNetworkException()
    {
        // HttpRequestException (DNS failure, connection refused, server
        // disappearing): ResourceHttp catches inside and returns Fail.
        // The plotter UI doesn't crash on a flaky helm-LAN.
        var http = ApiTestHelpers.MockClient(req => throw new HttpRequestException("nope"));
        var api = new NotificationsApi(http, ApiTestHelpers.FixedBaseUrl());

        var r = await api.AcknowledgeAsync("anything");

        await Assert.That(r.Success).IsFalse();
    }
}
