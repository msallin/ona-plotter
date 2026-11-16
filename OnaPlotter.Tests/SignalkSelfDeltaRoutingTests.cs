using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OnaPlotter.Models;
using OnaPlotter.Services;
using OnaPlotter.Services.Api;
using OnaPlotter.Services.ServerNotifications;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins how a self-context delta is routed: which path lands in which
/// store, which shapes are accepted, when <c>OnDataChanged</c> fires and
/// when the own track is sampled. Driven end-to-end through
/// <c>ProcessMessage</c> so it holds across any internal reshuffle of
/// the routing code.
/// </summary>
public class SignalkSelfDeltaRoutingTests
{
    private sealed class FakeBaseUrl : ISignalKBaseUrl
    {
        public string BaseUrl => "http://test.local";
        public Uri StreamUri(string subscribe = "none") => new("ws://test.local");
        public event Action? OnBaseUrlChanged { add { } remove { } }
        public string Combine(string path) => BaseUrl + path;
    }

    private sealed class Rig
    {
        public required SignalkClient Client { get; init; }
        public required AisStore Ais { get; init; }
        public required TrackBuffer Track { get; init; }
        public required ServerNotificationStore Notifications { get; init; }
        public required FakeTimeProvider Time { get; init; }
        public int DataChanged;
        public NavigationData Data => Client.Data;

        public void Self(string path, string valueJson) =>
            Client.ProcessMessage(
                $$"""{"context":"vessels.self","updates":[{"timestamp":"2025-01-01T12:00:00.000Z","values":[{"path":"{{path}}","value":{{valueJson}}}]}]}""");

        public void SelfNoValue(string path) =>
            Client.ProcessMessage(
                $$"""{"context":"vessels.self","updates":[{"values":[{"path":"{{path}}"}]}]}""");
    }

    private static Rig NewRig()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(TestClock.FixedUtcNow));
        var ais = new AisStore(time);
        var track = new TrackBuffer(time);
        var notifs = new ServerNotificationStore();
        var client = new SignalkClient(
            baseUrl: new FakeBaseUrl(),
            loggerFactory: NullLoggerFactory.Instance,
            data: new OnaPlotter.Models.NavigationData(time),
            track: track,
            ais: ais,
            http: new HttpClient(),
            settings: new FakeSettings(),
            serverNotifs: notifs,
            atons: new AtonStore(),
            time: time);
        var rig = new Rig { Client = client, Ais = ais, Track = track, Notifications = notifs, Time = time };
        client.OnDataChanged += () => rig.DataChanged++;
        return rig;
    }

    // --- position -----------------------------------------------------

    [Test]
    public async Task Position_Object_AppliesAndFires()
    {
        var r = NewRig();
        r.Self("navigation.position", """{"latitude":59.91,"longitude":10.75}""");

        await Assert.That(r.Data.Latitude).IsEqualTo(59.91);
        await Assert.That(r.Data.Longitude).IsEqualTo(10.75);
        await Assert.That(r.DataChanged).IsEqualTo(1);
    }

    [Test]
    public async Task Position_Malformed_IgnoredWithoutEvent()
    {
        var r = NewRig();
        r.Self("navigation.position", """{"latitude":59.91}""");
        r.Self("navigation.position", "\"59.91,10.75\"");
        r.Self("navigation.position", "null");

        await Assert.That(r.Data.Latitude).IsNull();
        await Assert.That(r.DataChanged).IsEqualTo(0);
    }

    // --- own track sampling --------------------------------------------

    [Test]
    public async Task Track_SampledOnFirstFix_ThenEveryFiveSeconds()
    {
        var r = NewRig();
        r.Self("navigation.position", """{"latitude":1,"longitude":2}""");
        await Assert.That(r.Track.Count).IsEqualTo(1);

        r.Time.Advance(TimeSpan.FromSeconds(4.9));
        r.Self("navigation.position", """{"latitude":1.1,"longitude":2}""");
        await Assert.That(r.Track.Count).IsEqualTo(1);

        r.Time.Advance(TimeSpan.FromSeconds(0.1));
        r.Self("navigation.speedOverGround", "3.5");
        await Assert.That(r.Track.Count).IsEqualTo(2);

        var last = r.Track.GetSnapshot()[^1];
        await Assert.That(last.Timestamp).IsEqualTo(TestClock.FixedUtcNow.AddSeconds(5));
        await Assert.That(last.Latitude).IsEqualTo(1.1);
        await Assert.That(last.SpeedOverGround).IsEqualTo(3.5);
    }

    [Test]
    public async Task Track_NotSampled_WithoutFix_OrWithoutChange()
    {
        var r = NewRig();
        r.Self("navigation.speedOverGround", "3.5");
        await Assert.That(r.Track.Count).IsEqualTo(0);

        r.Self("navigation.position", """{"latitude":1,"longitude":2}""");
        r.Time.Advance(TimeSpan.FromSeconds(10));
        r.Self("some.unknown.path", "1");
        await Assert.That(r.Track.Count).IsEqualTo(1);
    }

    // --- callsign -------------------------------------------------------

    [Test]
    public async Task Callsign_StoredWithoutDataChanged()
    {
        var r = NewRig();
        r.Self("communication.callsignVhf", "\"LA1234\"");

        await Assert.That(r.Client.OwnCallsign).IsEqualTo("LA1234");
        await Assert.That(r.DataChanged).IsEqualTo(0);
    }

    // --- anchor ---------------------------------------------------------

    [Test]
    public async Task Anchor_Position_Sets_ThenNullClears()
    {
        var r = NewRig();
        r.Self("navigation.anchor.position", """{"latitude":50.1,"longitude":-1.2}""");
        await Assert.That(r.Data.AnchorLatitude).IsEqualTo(50.1);
        await Assert.That(r.Data.AnchorLongitude).IsEqualTo(-1.2);

        r.Self("navigation.anchor.position", "null");
        await Assert.That(r.Data.AnchorLatitude).IsNull();
        await Assert.That(r.DataChanged).IsEqualTo(2);
    }

    [Test]
    public async Task Anchor_Position_MissingValue_Clears()
    {
        var r = NewRig();
        r.Self("navigation.anchor.position", """{"latitude":50.1,"longitude":-1.2}""");
        r.SelfNoValue("navigation.anchor.position");

        await Assert.That(r.Data.AnchorLatitude).IsNull();
    }

    [Test]
    public async Task Anchor_Position_String_IsIgnored()
    {
        var r = NewRig();
        r.Self("navigation.anchor.position", """{"latitude":50.1,"longitude":-1.2}""");
        r.Self("navigation.anchor.position", "\"weighed\"");

        await Assert.That(r.Data.AnchorLatitude).IsEqualTo(50.1);
        await Assert.That(r.DataChanged).IsEqualTo(1);
    }

    [Test]
    public async Task Anchor_MaxRadius_NumberApplies_NullClearsAnchor()
    {
        var r = NewRig();
        r.Self("navigation.anchor.position", """{"latitude":50.1,"longitude":-1.2}""");
        r.Self("navigation.anchor.maxRadius", "45");
        await Assert.That(r.Data.AnchorMaxRadius).IsEqualTo(45.0);

        r.Self("navigation.anchor.maxRadius", "null");
        await Assert.That(r.Data.AnchorLatitude).IsNull();
        await Assert.That(r.Data.AnchorMaxRadius).IsNull();
    }

    // --- course ---------------------------------------------------------

    [Test]
    public async Task ActiveRoute_ParentObject_FansOutToLeaves()
    {
        var r = NewRig();
        r.Self("navigation.course.activeRoute",
            """{"href":"/resources/routes/abc","name":"Passage","reverse":false,"pointIndex":2,"pointTotal":7}""");

        await Assert.That(r.Data.ActiveRouteHref).IsEqualTo("/resources/routes/abc");
        await Assert.That(r.Data.ActiveRouteName).IsEqualTo("Passage");
        await Assert.That(r.Data.ActiveRoutePointIndex).IsEqualTo(2);
        await Assert.That(r.Data.ActiveRoutePointTotal).IsEqualTo(7);
        await Assert.That(r.DataChanged).IsEqualTo(1);
    }

    [Test]
    public async Task ActiveRoute_ParentObject_WithoutFields_StillFires()
    {
        var r = NewRig();
        r.Self("navigation.course.activeRoute", """{"reverse":true}""");

        await Assert.That(r.Data.ActiveRouteHref).IsNull();
        await Assert.That(r.DataChanged).IsEqualTo(1);
    }

    [Test]
    public async Task NextPoint_ParentObject_And_PositionLeaf()
    {
        var r = NewRig();
        r.Self("navigation.course.nextPoint",
            """{"type":"RoutePoint","position":{"latitude":10,"longitude":20}}""");
        await Assert.That(r.Data.CourseNextPointLatitude).IsEqualTo(10.0);
        await Assert.That(r.Data.CourseNextPointLongitude).IsEqualTo(20.0);

        r.Self("navigation.course.nextPoint.position", """{"latitude":11,"longitude":21}""");
        await Assert.That(r.Data.CourseNextPointLatitude).IsEqualTo(11.0);
        await Assert.That(r.DataChanged).IsEqualTo(2);
    }

    [Test]
    public async Task PreviousPoint_ParentObject_And_PositionLeaf()
    {
        var r = NewRig();
        r.Self("navigation.course.previousPoint",
            """{"type":"Location","position":{"latitude":-10,"longitude":-20}}""");
        await Assert.That(r.Data.CoursePreviousPointLatitude).IsEqualTo(-10.0);

        r.Self("navigation.course.previousPoint.position", """{"latitude":-11,"longitude":-21}""");
        await Assert.That(r.Data.CoursePreviousPointLongitude).IsEqualTo(-21.0);
    }

    [Test]
    [Arguments("navigation.course.activeRoute.href")]
    [Arguments("navigation.course.activeRoute")]
    [Arguments("navigation.course.nextPoint")]
    public async Task Course_NullParentOrHref_ClearsCourse(string path)
    {
        var r = NewRig();
        r.Self("navigation.course.activeRoute",
            """{"href":"/resources/routes/abc","name":"Passage","pointIndex":0,"pointTotal":3}""");
        r.Self("navigation.course.nextPoint.position", """{"latitude":1,"longitude":2}""");

        r.Self(path, "null");

        await Assert.That(r.Data.ActiveRouteHref).IsNull();
        await Assert.That(r.Data.CourseNextPointLatitude).IsNull();
        await Assert.That(r.DataChanged).IsEqualTo(3);
    }

    [Test]
    public async Task Course_HrefString_AppliesThroughStringPath()
    {
        var r = NewRig();
        r.Self("navigation.course.activeRoute.href", "\"/resources/routes/xyz\"");

        await Assert.That(r.Data.ActiveRouteHref).IsEqualTo("/resources/routes/xyz");
        await Assert.That(r.DataChanged).IsEqualTo(1);
    }

    // --- course-provider notifications ----------------------------------

    [Test]
    [Arguments("""{"state":"alert","method":["visual"],"message":"Arrived"}""", true)]
    [Arguments("""{"state":"emergency"}""", true)]
    [Arguments("""{"state":"normal"}""", false)]
    [Arguments("""{"state":"cleared"}""", false)]
    [Arguments("""{"message":"no state"}""", true)]
    [Arguments("true", true)]
    [Arguments("false", false)]
    [Arguments("null", false)]
    [Arguments("\"alert\"", false)]
    public async Task CourseNotification_MapsToArmedFlag(string valueJson, bool armed)
    {
        foreach (var path in new[]
                 {
                     "notifications.navigation.course.arrivalCircleEntered",
                     "notifications.navigation.course.perpendicularPassed",
                 })
        {
            var r = NewRig();
            r.Self(path, valueJson);

            var flag = path.EndsWith("arrivalCircleEntered", StringComparison.Ordinal)
                ? r.Data.ArrivalCircleEntered
                : r.Data.PerpendicularPassed;
            await Assert.That(flag).IsEqualTo(armed);
            await Assert.That(r.DataChanged).IsEqualTo(1);
            // Course flags never reach the generic notification store.
            await Assert.That(r.Notifications.Count).IsEqualTo(0);
        }
    }

    // --- generic notifications ----------------------------------------

    [Test]
    public async Task ServerNotification_ArmsAndClears_FiringOnlyOnChange()
    {
        var r = NewRig();
        r.Self("notifications.mob", """{"state":"emergency","message":"MOB"}""");
        await Assert.That(r.Notifications.Contains("notifications.mob")).IsTrue();
        await Assert.That(r.DataChanged).IsEqualTo(1);

        r.Self("notifications.mob", "null");
        await Assert.That(r.Notifications.Contains("notifications.mob")).IsFalse();
        await Assert.That(r.DataChanged).IsEqualTo(2);

        // Clearing an unknown path is not a change.
        r.Self("notifications.other", "null");
        await Assert.That(r.DataChanged).IsEqualTo(2);
    }

    // --- radar ----------------------------------------------------------

    [Test]
    public async Task Radar_TargetUnderSelf_LandsInAisStore_WithoutDataChanged()
    {
        var r = NewRig();
        r.Self("radars.r1.targets.7",
            """{"position":{"latitude":1,"longitude":2},"motion":{"course":1.5,"speed":3}}""");

        var v = r.Ais.GetVessels().Single();
        await Assert.That(v.Context).IsEqualTo("radar.r1.7");
        await Assert.That(v.Latitude).IsEqualTo(1.0);
        await Assert.That(v.SpeedOverGround).IsEqualTo(3.0);
        await Assert.That(r.DataChanged).IsEqualTo(0);
    }

    // --- generic fallbacks ---------------------------------------------

    [Test]
    public async Task Bool_KnownPath_Applies()
    {
        var r = NewRig();
        r.Self("navigation.course.calcValues.arrivalCircleEntered", "true");

        await Assert.That(r.Data.ArrivalCircleEntered).IsTrue();
        await Assert.That(r.DataChanged).IsEqualTo(1);
    }

    [Test]
    public async Task String_KnownPath_Applies()
    {
        var r = NewRig();
        r.Self("steering.autopilot.state", "\"auto\"");

        await Assert.That(r.Data.AutopilotState).IsEqualTo("auto");
        await Assert.That(r.DataChanged).IsEqualTo(1);
    }

    [Test]
    public async Task Number_KnownPath_Applies()
    {
        var r = NewRig();
        r.Self("environment.depth.belowSurface", "12.5");
        r.Self("navigation.magneticVariation", "0.05");

        await Assert.That(r.Data.DepthBelowSurface).IsEqualTo(12.5);
        await Assert.That(r.Data.MagneticVariation).IsEqualTo(0.05);
        await Assert.That(r.DataChanged).IsEqualTo(2);
    }

    [Test]
    public async Task UnknownPath_OrWrongShape_NoEvent()
    {
        var r = NewRig();
        r.Self("some.unknown.path", "1");
        r.Self("some.unknown.flag", "true");
        r.Self("some.unknown.text", "\"x\"");
        r.Self("navigation.speedOverGround", "\"fast\"");
        r.Self("navigation.speedOverGround", """{"a":1}""");

        await Assert.That(r.DataChanged).IsEqualTo(0);
        await Assert.That(r.Data.SpeedOverGround).IsNull();
    }

    [Test]
    public async Task EveryUpdate_MarksDataReceived_EvenWithoutValues()
    {
        var r = NewRig();
        r.Client.ProcessMessage("""{"context":"vessels.self","updates":[{"timestamp":"2025-01-01T12:00:00.000Z"}]}""");

        await Assert.That(r.Data.LastReceivedUtc).IsNotNull();
        await Assert.That(r.DataChanged).IsEqualTo(0);
    }

    [Test]
    public async Task MultipleValues_InOneDelta_FireOnce()
    {
        var r = NewRig();
        r.Client.ProcessMessage("""
            {"context":"vessels.self","updates":[
              {"values":[
                {"path":"navigation.speedOverGround","value":3.1},
                {"path":"navigation.headingTrue","value":1.2}]},
              {"values":[
                {"path":"environment.wind.speedApparent","value":7.0}]}]}
            """);

        await Assert.That(r.Data.SpeedOverGround).IsEqualTo(3.1);
        await Assert.That(r.Data.HeadingTrue).IsEqualTo(1.2);
        await Assert.That(r.Data.WindSpeedApparent).IsEqualTo(7.0);
        await Assert.That(r.DataChanged).IsEqualTo(1);
    }

    [Test]
    public async Task UrnSelfContext_RoutesAsSelf_OnceKnown()
    {
        var r = NewRig();
        r.Client.SetSelfContext("urn:mrn:imo:mmsi:123456789");
        r.Client.ProcessMessage("""
            {"context":"vessels.urn:mrn:imo:mmsi:123456789","updates":[
              {"values":[{"path":"navigation.speedOverGround","value":2.0}]}]}
            """);

        await Assert.That(r.Data.SpeedOverGround).IsEqualTo(2.0);
        await Assert.That(r.Ais.GetVessels()).IsEmpty();
    }

    [Test]
    public async Task OtherVesselContext_GoesToAis_NotSelf()
    {
        var r = NewRig();
        r.Client.ProcessMessage("""
            {"context":"vessels.urn:mrn:imo:mmsi:987654321","updates":[
              {"timestamp":"2025-01-01T11:59:00.000Z","values":[
                {"path":"navigation.position","value":{"latitude":5,"longitude":6}},
                {"path":"navigation.speedOverGround","value":4.0}]}]}
            """);

        await Assert.That(r.Data.SpeedOverGround).IsNull();
        await Assert.That(r.DataChanged).IsEqualTo(0);
        var v = r.Ais.GetVessels().Single();
        await Assert.That(v.Mmsi).IsEqualTo("987654321");
        await Assert.That(v.SpeedOverGround).IsEqualTo(4.0);
        // The wire timestamp, not arrival time, drives AIS staleness.
        await Assert.That(v.LastAisSeen).IsEqualTo(TestClock.FixedUtcNow.AddMinutes(-1));
    }
}
