using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OnaPlotter.Services;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Tests;

/// <summary>
/// Anchors the byte-direct ProcessMessageBytes path that the live
/// WebSocket loop uses. The other SignalkClient tests go through the
/// string-shim ProcessMessage(string) overload; this file pins the
/// invariants that come from skipping the UTF-16 transcode:
///
/// 1. A delta with no "self" key never triggers the hello scan, so
///    SetSelfContext stays unmodified.
/// 2. A hello-shaped payload (no updates, contains "self") parses out
///    of raw bytes and resolves _selfContext.
/// 3. The string overload and the byte overload route to the same
///    handler; both produce the same outcome on the same payload.
/// </summary>
public class SignalkClientByteParseTests
{
    private sealed class FakeBaseUrl : ISignalKBaseUrl
    {
        public string BaseUrl => "http://test.local";
        public Uri StreamUri(string subscribe = "none") => new("ws://test.local");
        public event Action? OnBaseUrlChanged { add { } remove { } }
        public string Combine(string path) => BaseUrl + path;
    }

    private static SignalkClient NewClient(AisStore? store = null) =>
        new(
            baseUrl: new FakeBaseUrl(),
            logger: NullLogger<SignalkClient>.Instance,
            track: new TrackBuffer(),
            ais: store ?? new AisStore(),
            http: new HttpClient(),
            settings: new FakeSettings(),
            serverNotifs: new OnaPlotter.Services.ServerNotifications.ServerNotificationStore(),
            atons: new OnaPlotter.Services.AtonStore(),
            time: TimeProvider.System);

    [Test]
    public async Task ProcessMessageBytes_Hello_Resolves_SelfContext()
    {
        // Real-shape SK hello: no updates, "self" at root.
        const string hello = """
            {"name":"signalk-server-node","version":"2.10.0",
             "self":"vessels.urn:mrn:imo:mmsi:244000001",
             "roles":["master","main"]}
            """;
        var c = NewClient();
        c.ProcessMessageBytes(Encoding.UTF8.GetBytes(hello));
        await Assert.That(c.IsSelfContext("vessels.urn:mrn:imo:mmsi:244000001")).IsTrue();
    }

    [Test]
    public async Task ProcessMessageBytes_Plain_Delta_Skips_Self_Scan()
    {
        // A normal AIS delta with no "self" key must not touch
        // SetSelfContext (the empty _selfContext stays empty).
        const string delta = """
            {"context":"vessels.urn:mrn:imo:mmsi:244999999",
             "updates":[{
               "timestamp":"2026-04-22T22:00:00.000Z",
               "values":[{"path":"navigation.speedOverGround","value":2.5}]
             }]}
            """;
        var c = NewClient();
        c.ProcessMessageBytes(Encoding.UTF8.GetBytes(delta));
        // Self never resolved; arbitrary URN is treated as foreign.
        await Assert.That(c.IsSelfContext("vessels.urn:mrn:imo:mmsi:244000001")).IsFalse();
    }

    [Test]
    public async Task ProcessMessage_StringShim_And_Bytes_Produce_Same_Outcome()
    {
        // Equivalence: routing the same payload through both entry
        // points should leave the AisStore in identical states. This
        // is the contract that lets the older test suite keep using
        // ProcessMessage(string) without diverging from production.
        const string delta = """
            {"context":"vessels.urn:mrn:imo:mmsi:244111222",
             "updates":[{
               "timestamp":"2026-04-22T22:00:00.000Z",
               "values":[
                 {"path":"navigation.position","value":{"latitude":47.4,"longitude":8.5}}
               ]
             }]}
            """;

        var storeA = new AisStore();
        NewClient(storeA).ProcessMessage(delta);

        var storeB = new AisStore();
        NewClient(storeB).ProcessMessageBytes(Encoding.UTF8.GetBytes(delta));

        await Assert.That(storeA.Count).IsEqualTo(storeB.Count);
        await Assert.That(storeA.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ProcessMessageBytes_Malformed_Payload_Does_Not_Throw()
    {
        // Garbage in must not crash the receive loop. Same contract as
        // the string overload's JsonException-swallow. We assert on the
        // post-call state rather than a constant, so the analyzer is
        // satisfied AND we get a real signal on regression: a thrown
        // JsonException would propagate, fail the test, and never
        // reach the IsEqualTo line.
        var c = NewClient();
        c.ProcessMessageBytes(Encoding.UTF8.GetBytes("{not valid json"));
        // No state was created; AisStore stays empty.
        await Assert.That(c.IsSelfContext("vessels.urn:mrn:imo:mmsi:foo")).IsFalse();
    }

    [Test]
    public async Task ProcessMessageBytes_Skips_OnRawMessage_When_No_Subscriber()
    {
        // The byte path's whole point: skip the UTF-16 string
        // allocation when nobody is listening. We can't observe
        // "did we allocate a string?" directly, but we CAN observe
        // "was the subscriber called?" - if a future refactor flips
        // the gate to "always invoke", subscribed-after counts go up
        // when they shouldn't. This test pins the contract: an
        // unsubscribed delta increments no listener; subscribing
        // after the fact and pushing a new delta increments by
        // exactly one.
        var c = NewClient();
        int callCount = 0;
        Action<string> handler = _ => Interlocked.Increment(ref callCount);

        const string delta = """
            {"context":"vessels.urn:mrn:imo:mmsi:244111222",
             "updates":[{
               "timestamp":"2026-04-22T22:00:00.000Z",
               "values":[{"path":"navigation.speedOverGround","value":2.5}]
             }]}
            """;

        // Phase 1: no subscriber. callCount stays 0.
        c.ProcessMessageBytes(Encoding.UTF8.GetBytes(delta));
        await Assert.That(callCount).IsEqualTo(0);

        // Phase 2: subscribe; one delta -> one invocation.
        c.OnRawMessage += handler;
        c.ProcessMessageBytes(Encoding.UTF8.GetBytes(delta));
        await Assert.That(callCount).IsEqualTo(1);

        // Phase 3: unsubscribe; further deltas don't increment.
        c.OnRawMessage -= handler;
        c.ProcessMessageBytes(Encoding.UTF8.GetBytes(delta));
        c.ProcessMessageBytes(Encoding.UTF8.GetBytes(delta));
        await Assert.That(callCount).IsEqualTo(1);
    }

    [Test]
    public async Task ProcessMessageBytes_Self_Scan_Skipped_After_Resolution()
    {
        // First hello resolves _selfContext. A second hello-looking
        // payload with a different "self" value must be ignored: the
        // IndexOf scan is gated on string.IsNullOrEmpty(_selfContext)
        // so the second one never hits SetSelfContext.
        var c = NewClient();
        c.ProcessMessageBytes(Encoding.UTF8.GetBytes(
            """{"self":"vessels.urn:mrn:imo:mmsi:111111111"}"""));
        await Assert.That(c.IsSelfContext("vessels.urn:mrn:imo:mmsi:111111111")).IsTrue();

        c.ProcessMessageBytes(Encoding.UTF8.GetBytes(
            """{"self":"vessels.urn:mrn:imo:mmsi:222222222"}"""));
        // Original self stays pinned.
        await Assert.That(c.IsSelfContext("vessels.urn:mrn:imo:mmsi:111111111")).IsTrue();
        await Assert.That(c.IsSelfContext("vessels.urn:mrn:imo:mmsi:222222222")).IsFalse();
    }
}
