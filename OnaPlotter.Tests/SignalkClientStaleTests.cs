using Microsoft.Extensions.Logging.Abstractions;
using OnaPlotter.Services;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the IsDataStale behavior across the connection lifecycle.
/// The bug these tests cover: helms reported the connection chip
/// rendering "Stale" on first load even though dashboard cards were
/// updating, with the chip flipping to "Live" only on the next
/// navigation. Root cause was _lastMessageTicks defaulting to 0
/// (Unix epoch), so IsDataStale returned true the instant IsConnected
/// became true and stayed there until the chip's parent re-rendered
/// for an unrelated reason.
/// </summary>
public class SignalkClientStaleTests
{
    private sealed class FakeBaseUrl : ISignalKBaseUrl
    {
        public string BaseUrl => "http://test.local";
        public string RadarBaseUrl => BaseUrl;
        public Uri StreamUri(string subscribe = "none") => new("ws://test.local");
        public string Combine(string path) => BaseUrl + path;
        public string CombineRadar(string path) => BaseUrl + path;
    }

    private static SignalkClient NewClient()
        => new(
            baseUrl: new FakeBaseUrl(),
            logger: NullLogger<SignalkClient>.Instance,
            track: new TrackBuffer(),
            ais: new AisStore(),
            http: new HttpClient(),
            settings: new FakeSettings(),
            serverNotifs: new OnaPlotter.Services.ServerNotifications.ServerNotificationStore());

    [Test]
    public async Task IsDataStale_BeforeConnect_IsFalse()
    {
        // Disconnected client: IsDataStale guards on IsConnected so a
        // never-connected instance shouldn't report stale either.
        var c = NewClient();

        await Assert.That(c.IsConnected).IsFalse();
        await Assert.That(c.IsDataStale).IsFalse();
    }

    [Test]
    public async Task IsDataStale_RightAfterConnect_IsFalse()
    {
        // Reproduces the original first-load bug: connection just
        // opened, no deltas yet, IsDataStale must return false.
        // Without the MarkConnectionOpened seed, _lastMessageTicks
        // is 0 and the assertion below fails (IsDataStale=true).
        var c = NewClient();
        c.MarkConnectionOpened();

        await Assert.That(c.IsConnected).IsTrue();
        await Assert.That(c.IsDataStale).IsFalse();
    }

    [Test]
    public async Task IsDataStale_AfterProcessMessage_IsFalse()
    {
        // Once a delta has arrived, IsDataStale stays false (the
        // ProcessMessage-side seed was already in place; this test
        // makes sure the new connect-time seed doesn't disturb the
        // existing path).
        var c = NewClient();
        c.MarkConnectionOpened();
        c.ProcessMessage("{}");

        await Assert.That(c.IsDataStale).IsFalse();
    }
}
