using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using OnaPlotter.Models;
using OnaPlotter.Services.Api;
using OnaPlotter.Services.Signalk;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the SignalK v2 Course REST seeder's parsing in isolation.
/// SignalkClientCourseTests still drives the integration path (via
/// SignalkClient.SeedSelfCourseFromRestAsync, the test shim that
/// delegates here), so these tests focus on the seeder contract:
/// every recognised leaf lands on NavigationData, OnDataChanged
/// fires once when something seeded, 404 stays silent.
/// </summary>
public class SignalkCourseSeederTests
{
    private sealed class FakeBaseUrl : ISignalKBaseUrl
    {
        public string BaseUrl => "http://test.local";
        public Uri StreamUri(string subscribe = "none") => new("ws://test.local");
        public event Action? OnBaseUrlChanged { add { } remove { } }
        public string Combine(string path) => BaseUrl + path;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly Exception? _throw;

        public Uri? LastUri { get; private set; }

        public StubHandler(HttpStatusCode status, string body) { _status = status; _body = body; _throw = null; }
        public StubHandler(Exception toThrow) { _status = HttpStatusCode.OK; _body = ""; _throw = toThrow; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            if (_throw is not null) throw _throw;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    [Test]
    public async Task SeedAsync_FullCourseBody_PopulatesEveryLeafAndFiresOnce()
    {
        // The single fully-loaded body the production server emits
        // when a route is active. Pin the contract: every leaf lands
        // and OnDataChanged fires exactly once.
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
        var data = new NavigationData();
        int calls = 0;
        var handler = new StubHandler(HttpStatusCode.OK, body);
        var seeder = new SignalkCourseSeeder(
            new HttpClient(handler), new FakeBaseUrl(), data,
            NullLogger<SignalkCourseSeeder>.Instance,
            onDataChanged: () => calls++);

        await seeder.SeedAsync(CancellationToken.None);

        await Assert.That(data.ActiveRouteHref).IsEqualTo("/resources/routes/abc-123");
        await Assert.That(data.ActiveRouteName).IsEqualTo("Skye Passage");
        await Assert.That(data.ActiveRoutePointIndex).IsEqualTo(2);
        await Assert.That(data.ActiveRoutePointTotal).IsEqualTo(7);
        await Assert.That(data.CourseNextPointLatitude).IsEqualTo(58.41);
        await Assert.That(data.CourseNextPointLongitude).IsEqualTo(-6.27);
        await Assert.That(data.CoursePreviousPointLatitude).IsEqualTo(57.99);
        await Assert.That(data.CoursePreviousPointLongitude).IsEqualTo(-5.83);
        await Assert.That(data.HasActiveCourse).IsTrue();
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(handler.LastUri!.AbsolutePath)
            .IsEqualTo("/signalk/v2/api/vessels/self/navigation/course");
    }

    [Test]
    public async Task SeedAsync_NextPointOnly_FiresOnDataChanged()
    {
        // Older / partial server responses: just the nextPoint
        // present (no activeRoute object). Map still wants the marker.
        const string body = """
        {"nextPoint":{"position":{"latitude":12.0,"longitude":-34.0}}}
        """;
        var data = new NavigationData();
        int calls = 0;
        var seeder = new SignalkCourseSeeder(
            new HttpClient(new StubHandler(HttpStatusCode.OK, body)),
            new FakeBaseUrl(), data,
            NullLogger<SignalkCourseSeeder>.Instance,
            onDataChanged: () => calls++);

        await seeder.SeedAsync(CancellationToken.None);

        await Assert.That(data.CourseNextPointLatitude).IsEqualTo(12.0);
        await Assert.That(data.CourseNextPointLongitude).IsEqualTo(-34.0);
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task SeedAsync_EmptyObject_DoesNotFireOnDataChanged()
    {
        // Server replies {} (no active route): nothing to seed,
        // OnDataChanged stays silent so the UI doesn't redraw for
        // nothing.
        var data = new NavigationData();
        int calls = 0;
        var seeder = new SignalkCourseSeeder(
            new HttpClient(new StubHandler(HttpStatusCode.OK, "{}")),
            new FakeBaseUrl(), data,
            NullLogger<SignalkCourseSeeder>.Instance,
            onDataChanged: () => calls++);

        await seeder.SeedAsync(CancellationToken.None);

        await Assert.That(data.HasActiveCourse).IsFalse();
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task SeedAsync_404_DoesNotFireOnDataChanged()
    {
        // SignalK servers without the v2 Course API or course-provider
        // plugin return 404 - silent no-op at Debug level.
        var data = new NavigationData();
        int calls = 0;
        var seeder = new SignalkCourseSeeder(
            new HttpClient(new StubHandler(HttpStatusCode.NotFound, "")),
            new FakeBaseUrl(), data,
            NullLogger<SignalkCourseSeeder>.Instance,
            onDataChanged: () => calls++);

        await seeder.SeedAsync(CancellationToken.None);

        await Assert.That(data.HasActiveCourse).IsFalse();
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task SeedAsync_NonObjectRoot_DoesNotFireOnDataChanged()
    {
        // Defensive: the body returned by a misbehaving server
        // (array, scalar, malformed object) bails before any apply.
        var data = new NavigationData();
        int calls = 0;
        var seeder = new SignalkCourseSeeder(
            new HttpClient(new StubHandler(HttpStatusCode.OK, "[]")),
            new FakeBaseUrl(), data,
            NullLogger<SignalkCourseSeeder>.Instance,
            onDataChanged: () => calls++);

        await seeder.SeedAsync(CancellationToken.None);

        await Assert.That(data.HasActiveCourse).IsFalse();
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task SeedAsync_NextPointMissingPosition_DoesNotFireOnDataChanged()
    {
        // Defensive: a nextPoint object without latitude/longitude
        // shouldn't NaN out the map. Skip and stay silent.
        const string body = """
        {"nextPoint":{"type":"RoutePoint","position":{"latitude":"bad"}}}
        """;
        var data = new NavigationData();
        int calls = 0;
        var seeder = new SignalkCourseSeeder(
            new HttpClient(new StubHandler(HttpStatusCode.OK, body)),
            new FakeBaseUrl(), data,
            NullLogger<SignalkCourseSeeder>.Instance,
            onDataChanged: () => calls++);

        await seeder.SeedAsync(CancellationToken.None);

        await Assert.That(data.CourseNextPointLatitude).IsNull();
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task SeedAsync_TransportException_LogsButDoesNotThrow()
    {
        // Network failure must not crash the receive loop. The
        // catch-all logs at Warning and returns.
        var data = new NavigationData();
        int calls = 0;
        var seeder = new SignalkCourseSeeder(
            new HttpClient(new StubHandler(new HttpRequestException("boom"))),
            new FakeBaseUrl(), data,
            NullLogger<SignalkCourseSeeder>.Instance,
            onDataChanged: () => calls++);

        await seeder.SeedAsync(CancellationToken.None);

        await Assert.That(data.HasActiveCourse).IsFalse();
        await Assert.That(calls).IsEqualTo(0);
    }
}
