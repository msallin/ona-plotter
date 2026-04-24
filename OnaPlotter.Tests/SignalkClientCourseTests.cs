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
        public string RadarBaseUrl => BaseUrl;
        public Uri StreamUri(string subscribe = "none") => new("ws://test.local");
        public string Combine(string path) => BaseUrl + path;
        public string CombineRadar(string path) => BaseUrl + path;
    }

    private static SignalkClient NewClient()
    {
        var c = new SignalkClient(
            baseUrl: new FakeBaseUrl(),
            logger: NullLogger<SignalkClient>.Instance,
            track: new TrackBuffer(),
            ais: new AisStore(),
            http: new HttpClient(),
            settings: new FakeSettings());
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
}
