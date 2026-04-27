using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using OnaPlotter.Services;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the ProcessSelfDelta handling of <c>navigation.course.activeRoute</c>
/// and <c>navigation.course.nextPoint</c> / <c>.previousPoint</c> for the
/// parent-OBJECT form that signalk-server v2's course API actually
/// emits on state change. Before the parent-object branches existed,
/// OnA silently dropped the activation delta so externally-activated
/// routes (freeboard, REST, another plotter) never showed up.
/// </summary>
public class SignalkClientCourseTests
{
    private sealed class FakeBaseUrl : ISignalKBaseUrl
    {
        public string BaseUrl => "http://test.local";
        public Uri StreamUri(string subscribe = "none") => new("ws://test.local");
        public string Combine(string path) => BaseUrl + path;
    }

    private static SignalkClient NewClient()
    {
        var c = new SignalkClient(
            baseUrl: new FakeBaseUrl(),
            logger: NullLogger<SignalkClient>.Instance,
            track: new TrackBuffer(),
            ais: new AisStore(),
            http: new HttpClient(),
            settings: new FakeSettings(),
            serverNotifs: new OnaPlotter.Services.ServerNotifications.ServerNotificationStore(),
            atons: new OnaPlotter.Services.AtonStore(),
            time: TimeProvider.System);
        c.SetSelfContext("vessels.urn:mrn:imo:mmsi:261006533");
        return c;
    }

    private static string Delta(string path, string valueJson) => $@"{{
      ""context"":""vessels.urn:mrn:imo:mmsi:261006533"",
      ""updates"":[{{
        ""timestamp"":""2026-04-24T02:00:00.000Z"",
        ""values"":[{{ ""path"":""{path}"", ""value"":{valueJson} }}]
      }}]
    }}";

    [Test]
    public async Task ActiveRoute_ParentObject_Activation_ExtractsHrefNamePointIndex()
    {
        // The server publishes activation as ONE parent-object delta,
        // not four leaves. Confirmed by instrumented container test:
        // { href, name, reverse, pointIndex, pointTotal }.
        var c = NewClient();
        string value = """{"href":"/resources/routes/abc-123","name":"Passage to Skye","reverse":false,"pointIndex":0,"pointTotal":5}""";
        c.ProcessMessage(Delta("navigation.course.activeRoute", value));

        await Assert.That(c.Data.ActiveRouteHref).IsEqualTo("/resources/routes/abc-123");
        await Assert.That(c.Data.ActiveRouteName).IsEqualTo("Passage to Skye");
    }

    [Test]
    public async Task ActiveRoute_ParentNull_Deactivation_ClearsHref()
    {
        // Existing deactivation path must still work after the new
        // activation branch is added.
        var c = NewClient();
        c.ProcessMessage(Delta("navigation.course.activeRoute",
            """{"href":"/resources/routes/abc-123","name":"Foo","pointIndex":0,"pointTotal":3}"""));
        await Assert.That(c.Data.ActiveRouteHref).IsEqualTo("/resources/routes/abc-123");

        c.ProcessMessage(Delta("navigation.course.activeRoute", "null"));
        await Assert.That(c.Data.ActiveRouteHref).IsNull();
    }

    [Test]
    public async Task NextPoint_ParentObject_ExtractsNestedPosition()
    {
        // Server publishes nextPoint as a parent object on activation.
        // We must read the nested position.{lat,lon}.
        var c = NewClient();
        string value = """{"type":"RoutePoint","position":{"latitude":24.6,"longitude":-76.82}}""";
        c.ProcessMessage(Delta("navigation.course.nextPoint", value));

        await Assert.That(c.Data.CourseNextPointLatitude).IsEqualTo(24.6);
        await Assert.That(c.Data.CourseNextPointLongitude).IsEqualTo(-76.82);
    }

    [Test]
    public async Task NextPoint_LeafPositionPath_StillHandled()
    {
        // Some plugin variants emit the .position leaf directly. The
        // leaf-path branch must still fire in addition to the new
        // parent-object branch.
        var c = NewClient();
        string value = """{"latitude":12.5,"longitude":-45.0}""";
        c.ProcessMessage(Delta("navigation.course.nextPoint.position", value));

        await Assert.That(c.Data.CourseNextPointLatitude).IsEqualTo(12.5);
        await Assert.That(c.Data.CourseNextPointLongitude).IsEqualTo(-45.0);
    }

    [Test]
    public async Task PreviousPoint_ParentObject_ExtractsNestedPosition()
    {
        var c = NewClient();
        string value = """{"type":"VesselPosition","position":{"latitude":50.1,"longitude":-1.2}}""";
        c.ProcessMessage(Delta("navigation.course.previousPoint", value));

        await Assert.That(c.Data.CoursePreviousPointLatitude).IsEqualTo(50.1);
        await Assert.That(c.Data.CoursePreviousPointLongitude).IsEqualTo(-1.2);
    }

    [Test]
    public async Task ActivationFollowedByDeactivation_RoundTrip()
    {
        // Smoke: a full external-plotter interaction. Activate,
        // observe href set; deactivate, observe href cleared.
        var c = NewClient();
        c.ProcessMessage(Delta("navigation.course.activeRoute",
            """{"href":"/resources/routes/abc-123","name":"R","pointIndex":0,"pointTotal":3}"""));
        c.ProcessMessage(Delta("navigation.course.nextPoint",
            """{"type":"RoutePoint","position":{"latitude":50.0,"longitude":-1.0}}"""));
        await Assert.That(c.Data.HasActiveCourse).IsTrue();

        c.ProcessMessage(Delta("navigation.course.activeRoute", "null"));
        await Assert.That(c.Data.ActiveRouteHref).IsNull();
    }

    private sealed class JsonHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private static SignalkClient NewClientWithHttp(HttpClient http)
    {
        var c = new SignalkClient(
            baseUrl: new FakeBaseUrl(),
            logger: NullLogger<SignalkClient>.Instance,
            track: new TrackBuffer(),
            ais: new AisStore(),
            http: http,
            settings: new FakeSettings(),
            serverNotifs: new OnaPlotter.Services.ServerNotifications.ServerNotificationStore(),
            atons: new OnaPlotter.Services.AtonStore(),
            time: TimeProvider.System);
        c.SetSelfContext("vessels.urn:mrn:imo:mmsi:261006533");
        return c;
    }

    [Test]
    public async Task SeedFromRest_PopulatesActiveRouteAndFiresOnDataChanged()
    {
        // The seed exists specifically for the page-reload path: a
        // route that was already active on the SignalK server before
        // this client connected won't appear on the delta stream
        // (subscriptions only fire on change), so /v2/api/.../course
        // is the only way to learn about it. Pin the contract: a
        // success response with an activeRoute object lands every
        // expected leaf in NavigationData and notifies subscribers.
        const string body = """
        {
          "activeRoute": {
            "href":"/resources/routes/abc-123",
            "name":"Skye Passage",
            "pointIndex":2,
            "pointTotal":7
          },
          "nextPoint": {
            "type":"RoutePoint",
            "position":{"latitude":58.41,"longitude":-6.27}
          },
          "previousPoint": {
            "type":"VesselPosition",
            "position":{"latitude":57.99,"longitude":-5.83}
          }
        }
        """;
        var handler = new JsonHandler(body);
        var c = NewClientWithHttp(new HttpClient(handler));
        bool fired = false;
        c.OnDataChanged += () => fired = true;

        await c.SeedSelfCourseFromRestAsync(CancellationToken.None);

        await Assert.That(c.Data.ActiveRouteHref).IsEqualTo("/resources/routes/abc-123");
        await Assert.That(c.Data.ActiveRouteName).IsEqualTo("Skye Passage");
        await Assert.That(c.Data.ActiveRoutePointIndex).IsEqualTo(2);
        await Assert.That(c.Data.ActiveRoutePointTotal).IsEqualTo(7);
        await Assert.That(c.Data.CourseNextPointLatitude).IsEqualTo(58.41);
        await Assert.That(c.Data.CourseNextPointLongitude).IsEqualTo(-6.27);
        await Assert.That(c.Data.CoursePreviousPointLatitude).IsEqualTo(57.99);
        await Assert.That(c.Data.CoursePreviousPointLongitude).IsEqualTo(-5.83);
        await Assert.That(c.Data.HasActiveCourse).IsTrue();
        await Assert.That(fired).IsTrue();
        await Assert.That(handler.LastUri!.AbsolutePath)
            .IsEqualTo("/signalk/v2/api/vessels/self/navigation/course");
    }

    [Test]
    public async Task SeedFromRest_SubscriberAddedAfterSeed_StillSeesActiveCourse()
    {
        // The Map page subscribes to OnDataChanged late (after a slow
        // sequence of REST seeds and JS interop). If the SignalK seed
        // races ahead and fires before the page subscribes, the
        // subscriber misses the event -- but it still must be able to
        // observe the seeded state on Data when its own kick runs.
        // This is the contract Map.razor relies on for the catch-up
        // HandleDataChanged() call after subscribing.
        const string body = """
        {"activeRoute":{"href":"/resources/routes/x","name":"X","pointIndex":0,"pointTotal":2},
         "nextPoint":{"position":{"latitude":1.0,"longitude":2.0}}}
        """;
        var c = NewClientWithHttp(new HttpClient(new JsonHandler(body)));

        await c.SeedSelfCourseFromRestAsync(CancellationToken.None);

        // Late subscriber: did NOT see the OnDataChanged from the seed,
        // but still finds the state on Data.
        bool lateFired = false;
        c.OnDataChanged += () => lateFired = true;
        await Assert.That(c.Data.HasActiveCourse).IsTrue();
        await Assert.That(c.Data.ActiveRouteHref).IsEqualTo("/resources/routes/x");
        await Assert.That(lateFired).IsFalse();
    }

    [Test]
    public async Task SeedFromRest_NotFound_DoesNotFireOnDataChanged()
    {
        // Some plotters / older SignalK servers return 404 on the v2
        // course endpoint when no route is active. The seed must
        // tolerate this silently -- no exception, no spurious
        // OnDataChanged that would trigger redraw work.
        var c = NewClientWithHttp(new HttpClient(new JsonHandler("", HttpStatusCode.NotFound)));
        bool fired = false;
        c.OnDataChanged += () => fired = true;

        await c.SeedSelfCourseFromRestAsync(CancellationToken.None);

        await Assert.That(c.Data.ActiveRouteHref).IsNull();
        await Assert.That(c.Data.HasActiveCourse).IsFalse();
        await Assert.That(fired).IsFalse();
    }
}
