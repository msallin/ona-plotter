using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OnaPlotter.Models;
using OnaPlotter.Services;
using OnaPlotter.Services.ServerNotifications;
using OnaPlotter.Services.Signalk;

namespace OnaPlotter.Tests.Services.Signalk;

/// <summary>
/// Context routing, self identity and resource fan-out of
/// <see cref="SignalkDeltaDispatcher"/>. Per-path self routing is pinned
/// end-to-end by <c>SignalkSelfDeltaRoutingTests</c>.
/// </summary>
public class SignalkDeltaDispatcherTests
{
    private sealed record Rig(
        SignalkDeltaDispatcher Dispatcher,
        NavigationData Data,
        AisStore Ais,
        AtonStore Atons,
        ServerNotificationStore Notifications,
        TrackBuffer Track);

    private static Rig NewRig()
    {
        var data = new NavigationData();
        var ais = new AisStore();
        var atons = new AtonStore();
        var notifs = new ServerNotificationStore();
        var track = new TrackBuffer();
        var d = new SignalkDeltaDispatcher(data, track, ais, atons, notifs,
            new FakeTimeProvider(new DateTimeOffset(TestClock.FixedUtcNow)), NullLogger.Instance);
        return new Rig(d, data, ais, atons, notifs, track);
    }

    private static bool Dispatch(Rig r, string json) => r.Dispatcher.Dispatch(Encoding.UTF8.GetBytes(json));

    [Test]
    public async Task Hello_SetsSelfContext_AndEvictsOwnBoatFromAis()
    {
        var r = NewRig();
        Dispatch(r, """{"context":"vessels.urn:mrn:imo:mmsi:111111111","updates":[{"values":[{"path":"navigation.speedOverGround","value":1}]}]}""");
        await Assert.That(r.Ais.Count).IsEqualTo(1);

        bool changed = Dispatch(r, """{"name":"signalk-server","version":"2.13.0","self":"vessels.urn:mrn:imo:mmsi:111111111"}""");

        await Assert.That(changed).IsFalse();
        await Assert.That(r.Ais.Count).IsEqualTo(0);
        await Assert.That(r.Dispatcher.OwnMmsi).IsEqualTo("111111111");
        await Assert.That(r.Dispatcher.IsSelfContext("vessels.urn:mrn:imo:mmsi:111111111")).IsTrue();
    }

    [Test]
    [Arguments(null, true)]
    [Arguments("", true)]
    [Arguments("vessels.self", true)]
    [Arguments("vessels.urn:mrn:imo:mmsi:222222222", false)]
    [Arguments("atons.urn:mrn:imo:mmsi:992222222", false)]
    public async Task IsSelfContext_BeforeIdentityKnown(string? context, bool expected)
    {
        await Assert.That(NewRig().Dispatcher.IsSelfContext(context)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("urn:mrn:imo:mmsi:333333333")]
    [Arguments("vessels.urn:mrn:imo:mmsi:333333333")]
    public async Task SetSelfContext_NormalisesPrefix(string raw)
    {
        var r = NewRig();
        r.Dispatcher.SetSelfContext(raw);

        await Assert.That(r.Dispatcher.IsSelfContext("vessels.urn:mrn:imo:mmsi:333333333")).IsTrue();
        await Assert.That(r.Dispatcher.OwnMmsi).IsEqualTo("333333333");
    }

    [Test]
    public async Task SetSelfContext_Blank_ResetsIdentity()
    {
        var r = NewRig();
        r.Dispatcher.SetSelfContext("urn:mrn:imo:mmsi:333333333");
        r.Dispatcher.SetSelfContext("  ");

        await Assert.That(r.Dispatcher.OwnMmsi).IsNull();
        await Assert.That(r.Dispatcher.IsSelfContext("vessels.urn:mrn:imo:mmsi:333333333")).IsFalse();
    }

    [Test]
    public async Task AtonContext_RoutesToAtonStore_NotSelfOrAis()
    {
        var r = NewRig();
        bool changed = Dispatch(r, """
            {"context":"atons.urn:mrn:imo:mmsi:992351234","updates":[{"values":[
              {"path":"navigation.position","value":{"latitude":51.5,"longitude":1.0}},
              {"path":"virtual","value":true}]}]}
            """);

        await Assert.That(changed).IsFalse();
        await Assert.That(r.Atons.Count).IsEqualTo(1);
        await Assert.That(r.Ais.Count).IsEqualTo(0);
        await Assert.That(r.Data.Latitude).IsNull();
    }

    [Test]
    public async Task AtonVirtual_WireNull_ClearsFlag()
    {
        var r = NewRig();
        Dispatch(r, """{"context":"atons.urn:mrn:imo:mmsi:992351234","updates":[{"values":[{"path":"navigation.position","value":{"latitude":51.5,"longitude":1.0}},{"path":"virtual","value":true}]}]}""");
        Dispatch(r, """{"context":"atons.urn:mrn:imo:mmsi:992351234","updates":[{"values":[{"path":"virtual","value":null}]}]}""");

        await Assert.That(r.Atons.GetAtons().Single().Virtual).IsNull();
    }

    [Test]
    public async Task ResetOnDisconnect_DropsNotificationsAndAtons_KeepsAis()
    {
        var r = NewRig();
        Dispatch(r, """{"context":"vessels.self","updates":[{"values":[{"path":"notifications.mob","value":{"state":"emergency"}}]}]}""");
        Dispatch(r, """{"context":"atons.urn:mrn:imo:mmsi:992351234","updates":[{"values":[{"path":"name","value":"BUOY"}]}]}""");
        Dispatch(r, """{"context":"vessels.urn:mrn:imo:mmsi:444444444","updates":[{"values":[{"path":"name","value":"X"}]}]}""");

        r.Dispatcher.ResetOnDisconnect();

        await Assert.That(r.Notifications.Count).IsEqualTo(0);
        await Assert.That(r.Atons.Count).IsEqualTo(0);
        await Assert.That(r.Ais.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Resources_FanOut_TypeIdAndValue_AnyContext()
    {
        var r = NewRig();
        var seen = new List<(string Type, string Id, JsonValueKind Kind)>();
        r.Dispatcher.OnResourceDelta += (type, id, value) => seen.Add((type, id, value.ValueKind));

        Dispatch(r, """
            {"context":"vessels.urn:mrn:imo:mmsi:555555555","updates":[{"values":[
              {"path":"resources.routes.urn:mrn:signalk:uuid:1a2b","value":{"name":"R"}},
              {"path":"resources.notes.n1","value":null},
              {"path":"resources.routes","value":{}},
              {"path":"resources.routes.","value":{}},
              {"path":"navigation.speedOverGround","value":1}]}]}
            """);

        await Assert.That(seen).IsEquivalentTo(new[]
        {
            ("routes", "urn:mrn:signalk:uuid:1a2b", JsonValueKind.Object),
            ("notes", "n1", JsonValueKind.Null),
        });
    }

    [Test]
    public async Task Resources_ThrowingHandler_DoesNotStopRouting()
    {
        var r = NewRig();
        r.Dispatcher.OnResourceDelta += (_, _, _) => throw new InvalidOperationException("boom");

        bool changed = Dispatch(r, """
            {"context":"vessels.self","updates":[{"values":[
              {"path":"resources.routes.r1","value":{"name":"R"}},
              {"path":"navigation.speedOverGround","value":2.5}]}]}
            """);

        await Assert.That(changed).IsTrue();
        await Assert.That(r.Data.SpeedOverGround).IsEqualTo(2.5);
    }

    [Test]
    public async Task MalformedJson_Throws_ForCallerToHandle()
    {
        var r = NewRig();
        await Assert.That(() => Dispatch(r, "{not json")).Throws<JsonException>();
    }
}
