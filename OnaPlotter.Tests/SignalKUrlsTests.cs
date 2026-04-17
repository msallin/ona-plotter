using OnaPlotter.Services.Api;

namespace OnaPlotter.Tests;

public class SignalKUrlsTests
{
    [Test]
    public async Task Route_UrlEncodesId()
    {
        // Weird IDs with slashes or spaces shouldn't break the URL.
        var url = SignalKUrls.Route("abc/xyz id");
        await Assert.That(url).IsEqualTo("/signalk/v2/api/resources/routes/abc%2Fxyz%20id");
    }

    [Test]
    public async Task Waypoint_UrlEncodesId()
    {
        var url = SignalKUrls.Waypoint("uuid-1234");
        await Assert.That(url).IsEqualTo("/signalk/v2/api/resources/waypoints/uuid-1234");
    }

    [Test]
    public async Task Track_UrlEncodesArgs()
    {
        var url = SignalKUrls.Track("1d", "30s");
        await Assert.That(url).IsEqualTo("/signalk/v1/api/self/track?timespan=1d&resolution=30s");
    }

    [Test]
    public async Task StreamWs_Http_ToWs()
    {
        var uri = SignalKUrls.StreamWs("http://example.local:3000");
        await Assert.That(uri.ToString())
            .IsEqualTo("ws://example.local:3000/signalk/v1/stream?subscribe=none");
    }

    [Test]
    public async Task StreamWs_Https_ToWss()
    {
        // Uri.ToString() strips the default wss port 443.
        var uri = SignalKUrls.StreamWs("https://example.com:443", "self");
        await Assert.That(uri.Scheme).IsEqualTo("wss");
        await Assert.That(uri.Host).IsEqualTo("example.com");
        await Assert.That(uri.PathAndQuery).IsEqualTo("/signalk/v1/stream?subscribe=self");
    }

    [Test]
    public async Task ExtractRouteId_FullPath()
    {
        var id = SignalKUrls.ExtractRouteId("/resources/routes/abc-123");
        await Assert.That(id).IsEqualTo("abc-123");
    }

    [Test]
    public async Task ExtractRouteId_SignalKPath()
    {
        var id = SignalKUrls.ExtractRouteId("/signalk/v2/api/resources/routes/uuid");
        await Assert.That(id).IsEqualTo("uuid");
    }

    [Test]
    public async Task ExtractRouteId_BareId_Unchanged()
    {
        var id = SignalKUrls.ExtractRouteId("just-the-id");
        await Assert.That(id).IsEqualTo("just-the-id");
    }
}
