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

    [Test]
    public async Task RadarSpokeWs_Http_ToWs_PreservesPort()
    {
        // The /spokes -> /stream path bug is the whole reason this
        // helper exists; pin the exact path so a typo doesn't slip
        // through silently.
        var uri = SignalKUrls.RadarSpokeWs("http://openplotter.local:3000", "nav0231A");
        await Assert.That(uri.ToString())
            .IsEqualTo("ws://openplotter.local:3000/signalk/v2/api/vessels/self/radars/nav0231A/stream");
    }

    [Test]
    public async Task RadarSpokeWs_Https_ToWss()
    {
        // wss derives from https; .Uri normalises 443 out of ToString().
        var uri = SignalKUrls.RadarSpokeWs("https://openplotter.local:443", "r");
        await Assert.That(uri.Scheme).IsEqualTo("wss");
        await Assert.That(uri.Host).IsEqualTo("openplotter.local");
        await Assert.That(uri.AbsolutePath).IsEqualTo("/signalk/v2/api/vessels/self/radars/r/stream");
    }

    [Test]
    public async Task RadarSpokeWs_EscapesRadarId()
    {
        // Radar ids in the wild are alphanum (Mayara emits "nav0231A"),
        // but the spec doesn't forbid weirder shapes. A slash-bearing
        // id must escape, otherwise it'd path-traverse out of /radars/.
        var uri = SignalKUrls.RadarSpokeWs("http://h:80", "a/b");
        await Assert.That(uri.AbsolutePath).IsEqualTo("/signalk/v2/api/vessels/self/radars/a%2Fb/stream");
    }

    [Test]
    public async Task IsSpokeUrlOnSameOrigin_Matching()
    {
        // The browser would happily open whatever URL the server
        // hands us; a same-origin guard is the only thing standing
        // between a hostile plugin and SSRF / exfil. Pin the
        // expected matrix.
        await Assert.That(SignalKUrls.IsSpokeUrlOnSameOrigin(
            "ws://h.local:3000/signalk/v2/api/vessels/self/radars/r/stream",
            "http://h.local:3000")).IsTrue();
        await Assert.That(SignalKUrls.IsSpokeUrlOnSameOrigin(
            "wss://h.local:443/signalk/v2/api/vessels/self/radars/r/stream",
            "https://h.local:443")).IsTrue();
    }

    [Test]
    public async Task IsSpokeUrlOnSameOrigin_RejectsCrossOrigin()
    {
        // Different host -- exfiltration vector.
        await Assert.That(SignalKUrls.IsSpokeUrlOnSameOrigin(
            "wss://attacker.example.com/signalk/v2/api/vessels/self/radars/r/stream",
            "https://h.local:443")).IsFalse();
        // Different port -- LAN pivot vector.
        await Assert.That(SignalKUrls.IsSpokeUrlOnSameOrigin(
            "ws://h.local:8080/signalk/v2/api/vessels/self/radars/r/stream",
            "http://h.local:3000")).IsFalse();
    }

    [Test]
    public async Task IsSpokeUrlOnSameOrigin_RejectsNonWsScheme()
    {
        // http(s):// might be a way to coerce the browser into
        // dispatching cookies on a non-WebSocket request. ws / wss
        // only.
        await Assert.That(SignalKUrls.IsSpokeUrlOnSameOrigin(
            "http://h.local:3000/signalk/v2/api/vessels/self/radars/r/stream",
            "http://h.local:3000")).IsFalse();
        await Assert.That(SignalKUrls.IsSpokeUrlOnSameOrigin(
            "javascript:alert(1)",
            "http://h.local:3000")).IsFalse();
        await Assert.That(SignalKUrls.IsSpokeUrlOnSameOrigin(
            "file:///etc/passwd",
            "http://h.local:3000")).IsFalse();
    }

    [Test]
    public async Task IsSpokeUrlOnSameOrigin_RejectsSchemeMismatch()
    {
        // ws against an https origin should be rejected -- the
        // helm's TLS posture must transit to wss.
        await Assert.That(SignalKUrls.IsSpokeUrlOnSameOrigin(
            "ws://h.local:443/signalk/v2/api/vessels/self/radars/r/stream",
            "https://h.local:443")).IsFalse();
    }

    [Test]
    public async Task IsSpokeUrlOnSameOrigin_NullEmptyMalformed()
    {
        await Assert.That(SignalKUrls.IsSpokeUrlOnSameOrigin(null!, "http://h.local:3000")).IsFalse();
        await Assert.That(SignalKUrls.IsSpokeUrlOnSameOrigin("", "http://h.local:3000")).IsFalse();
        await Assert.That(SignalKUrls.IsSpokeUrlOnSameOrigin("not a url", "http://h.local:3000")).IsFalse();
    }
}
