using System.Net;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Tests;

/// <summary>
/// Wire-shape contract for <see cref="AnchorAlarmApi"/>. Pin the URL +
/// HTTP method + JSON body the plugin actually expects so a future
/// rename or arg-shape drift breaks the test, not the helm trying to
/// drop anchor at sundown.
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
            // Read the body synchronously here -- the test doubles serve
            // a single request, so we don't worry about the small
            // .Result on the test thread.
            string body = req.Content?.ReadAsStringAsync().Result ?? "";
            log.Add(new CapturedRequest(req.Method.Method, req.RequestUri!.ToString(), body));
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, System.Text.Encoding.UTF8, "application/json"),
            };
        });
        return (client, log);
    }

    [Test]
    public async Task DropAsync_Posts_To_DropAnchor_Path_With_Radius_Body()
    {
        // Pinning the wire shape: POST {base}/plugins/anchoralarm/dropAnchor
        // with body {"radius": <metres>}. If a future plugin release
        // renames the path or expects "radiusMeters" instead, this test
        // fails loudly rather than the helm seeing "Drop failed: 404".
        var (client, log) = CapturingClient();
        var api = NewApi(client);

        var r = await api.DropAsync(25);

        await Assert.That(r.Success).IsTrue();
        await Assert.That(log.Count).IsEqualTo(1);
        await Assert.That(log[0].Method).IsEqualTo("POST");
        await Assert.That(log[0].Url).IsEqualTo(ApiTestHelpers.TestBase + "/plugins/anchoralarm/dropAnchor");
        // Body is JSON-encoded; check both the key and the integer value.
        await Assert.That(log[0].Body).Contains("\"radius\"");
        await Assert.That(log[0].Body).Contains("25");
    }

    [Test]
    public async Task DropAsync_404_Returns_Failure_With_Message()
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
    public async Task SetRadiusAsync_Posts_To_SetRadius_Path_With_Radius_Body()
    {
        // Used by the radius chips on the anchor HUD card after the
        // initial drop. Same shape as DropAsync's body so the JSON
        // serialiser path is shared; pin both so a refactor that
        // changes one doesn't silently change the other.
        var (client, log) = CapturingClient();
        var api = NewApi(client);

        var r = await api.SetRadiusAsync(50);

        await Assert.That(r.Success).IsTrue();
        await Assert.That(log[0].Url).IsEqualTo(ApiTestHelpers.TestBase + "/plugins/anchoralarm/setRadius");
        await Assert.That(log[0].Body).Contains("\"radius\"");
        await Assert.That(log[0].Body).Contains("50");
    }

    [Test]
    public async Task RaiseAsync_Posts_To_RaiseAnchor_Path()
    {
        // Body must still be JSON object (plugin requires Content-Type
        // application/json) but is otherwise empty per docs.
        var (client, log) = CapturingClient();
        var api = NewApi(client);

        var r = await api.RaiseAsync();

        await Assert.That(r.Success).IsTrue();
        await Assert.That(log[0].Method).IsEqualTo("POST");
        await Assert.That(log[0].Url).IsEqualTo(ApiTestHelpers.TestBase + "/plugins/anchoralarm/raiseAnchor");
    }
}
