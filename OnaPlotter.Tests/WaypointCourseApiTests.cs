using System.Net;
using OnaPlotter.Services.Api;
using OnaPlotter.Services.Settings;

namespace OnaPlotter.Tests;

public class WaypointCourseApiTests
{
    /// <summary>Stub INavPreferences for CourseApi tests. Returns a
    /// fixed arrival-circle so a regression in body emission shows
    /// up here (the live tests below don't assert the value
    /// individually; the dedicated arrivalCircle test does).</summary>
    private static INavPreferences NewNavPrefs(double arrivalCircleM = 50.0) =>
        new StubNavPrefs(arrivalCircleM);

    private sealed class StubNavPrefs : INavPreferences
    {
        public StubNavPrefs(double arrivalCircleM) { ArrivalCircleMeters = arrivalCircleM; }
        public bool PreferMagneticHeading => false;
        public bool PreferMagneticCourse => false;
        public bool AutoAdvanceWaypoints => true;
        public string SailingMode => "cruise";
        public string OwnVesselType => "power";
        public double ArrivalCircleMeters { get; }
        public string CogReadoutSource => "trueSmoothed";
        public string OwnCogVectorSource => "trueSmoothed";
        public Task SetPreferMagneticHeadingAsync(bool value) => Task.CompletedTask;
        public Task SetPreferMagneticCourseAsync(bool value) => Task.CompletedTask;
        public Task SetAutoAdvanceWaypointsAsync(bool value) => Task.CompletedTask;
        public Task SetSailingModeAsync(string value) => Task.CompletedTask;
        public Task SetOwnVesselTypeAsync(string value) => Task.CompletedTask;
        public Task SetArrivalCircleMetersAsync(double value) => Task.CompletedTask;
        public Task SetCogReadoutSourceAsync(string value) => Task.CompletedTask;
        public Task SetOwnCogVectorSourceAsync(string value) => Task.CompletedTask;
    }

    [Test]
    public async Task WaypointApi_GetAll_ParsesLatLon()
    {
        var body = """
        {
            "wp1": { "name": "Marker", "feature": { "type":"Feature",
                "geometry": { "type":"Point", "coordinates": [8.5, 47.4] } } }
        }
        """;
        var http = ApiTestHelpers.JsonClient(_ => body);
        var api = new WaypointApi(http, ApiTestHelpers.FixedBaseUrl());

        var waypoints = await api.GetAllAsync();

        await Assert.That(waypoints.Count).IsEqualTo(1);
        await Assert.That(waypoints[0].Id).IsEqualTo("wp1");
        await Assert.That(waypoints[0].Latitude).IsEqualTo(47.4);
        await Assert.That(waypoints[0].Longitude).IsEqualTo(8.5);
    }

    [Test]
    public async Task WaypointApi_Create_EmitsFeatureProperties()
    {
        // Pins the SignalK resource-API compatibility contract: every
        // POSTed waypoint must carry Feature.properties (name +
        // description) in addition to the top-level name and geometry.
        // A missing properties block is exactly what made our waypoints
        // invisible in peer SignalK clients until the route fix was
        // cargo-culted over.
        string? capturedBody = null;
        var http = ApiTestHelpers.MockClient(req =>
        {
            capturedBody = req.Content?.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("\"wpt-new-42\"")
            };
        });
        var api = new WaypointApi(http, ApiTestHelpers.FixedBaseUrl());

        var r = await api.CreateAsync("Marker", 47.4, 8.5);

        await Assert.That(r.Success).IsTrue();
        await Assert.That(r.Value).IsEqualTo("wpt-new-42");
        await Assert.That(capturedBody).IsNotNull();
        // Top-level name preserved for SignalK.
        await Assert.That(capturedBody).Contains("\"name\":\"Marker\"");
        // Feature type + Point geometry with [lon, lat] order.
        await Assert.That(capturedBody).Contains("\"type\":\"Feature\"");
        await Assert.That(capturedBody).Contains("\"type\":\"Point\"");
        await Assert.That(capturedBody).Contains("[8.5,47.4]");
        // Properties block with name + description (SignalK compat).
        await Assert.That(capturedBody).Contains("\"properties\"");
        await Assert.That(capturedBody).Contains("\"description\":\"\"");
    }

    [Test]
    public async Task WaypointApi_Delete_UrlEncodesId()
    {
        string? capturedUrl = null;
        var http = ApiTestHelpers.MockClient(req =>
        {
            capturedUrl = req.RequestUri?.AbsoluteUri;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var api = new WaypointApi(http, ApiTestHelpers.FixedBaseUrl());

        await api.DeleteAsync("id with space");

        await Assert.That(capturedUrl).EndsWith("/waypoints/id%20with%20space");
    }

    [Test]
    public async Task CourseApi_SetDestination_PutsHref()
    {
        string? capturedBody = null;
        HttpMethod? capturedMethod = null;
        var http = ApiTestHelpers.MockClient(req =>
        {
            capturedMethod = req.Method;
            capturedBody = req.Content?.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var api = new CourseApi(http, ApiTestHelpers.FixedBaseUrl(), NewNavPrefs());

        var r = await api.SetDestinationAsync("wpt-42");

        await Assert.That(r.Success).IsTrue();
        await Assert.That(capturedMethod).IsEqualTo(HttpMethod.Put);
        await Assert.That(capturedBody).Contains("/resources/waypoints/wpt-42");
    }

    [Test]
    public async Task CourseApi_SetActiveRoute_PutsExpectedShape()
    {
        // SignalK v2 compat: href uses the v1 relative resource path,
        // not the v2 full API path. pointIndex and reverse must be
        // serialised even at their defaults so the server sees the
        // expected shape. Without this, OnaPlotter can save a route
        // but has no way to start it.
        string? capturedUrl = null;
        string? capturedBody = null;
        HttpMethod? capturedMethod = null;
        var http = ApiTestHelpers.MockClient(req =>
        {
            capturedUrl = req.RequestUri?.AbsoluteUri;
            capturedMethod = req.Method;
            capturedBody = req.Content?.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var api = new CourseApi(http, ApiTestHelpers.FixedBaseUrl(), NewNavPrefs());

        var r = await api.SetActiveRouteAsync("rte-123", pointIndex: 2, reverse: true);

        await Assert.That(r.Success).IsTrue();
        await Assert.That(capturedMethod).IsEqualTo(HttpMethod.Put);
        await Assert.That(capturedUrl).EndsWith("/signalk/v2/api/vessels/self/navigation/course/activeRoute");
        await Assert.That(capturedBody).Contains("\"href\":\"/resources/routes/rte-123\"");
        await Assert.That(capturedBody).Contains("\"pointIndex\":2");
        await Assert.That(capturedBody).Contains("\"reverse\":true");
    }

    [Test]
    public async Task CourseApi_SetActiveRoute_EncodesIdWithSpecials()
    {
        string? capturedBody = null;
        var http = ApiTestHelpers.MockClient(req =>
        {
            capturedBody = req.Content?.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var api = new CourseApi(http, ApiTestHelpers.FixedBaseUrl(), NewNavPrefs());

        await api.SetActiveRouteAsync("my route/01");

        // The slash would otherwise split the href into two segments; URL
        // encoding keeps the id intact for server lookup.
        await Assert.That(capturedBody).Contains("/resources/routes/my%20route%2F01");
    }

    [Test]
    public async Task CourseApi_AdvanceActiveRoute_PutsNextPointEndpoint()
    {
        // SignalK v2 Course API: PUT /activeRoute/nextPoint with body
        // {"value": 1} advances one waypoint. The older empty-body
        // form is NOT spec-compliant; this pins the correct shape so
        // a drift back to {} doesn't regress auto-advance on strict
        // servers.
        string? capturedUrl = null;
        string? capturedBody = null;
        HttpMethod? capturedMethod = null;
        var http = ApiTestHelpers.MockClient(req =>
        {
            capturedUrl = req.RequestUri?.AbsoluteUri;
            capturedMethod = req.Method;
            capturedBody = req.Content?.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var api = new CourseApi(http, ApiTestHelpers.FixedBaseUrl(), NewNavPrefs());

        var r = await api.AdvanceActiveRouteAsync();

        await Assert.That(r.Success).IsTrue();
        await Assert.That(capturedMethod).IsEqualTo(HttpMethod.Put);
        await Assert.That(capturedUrl).EndsWith("/signalk/v2/api/vessels/self/navigation/course/activeRoute/nextPoint");
        await Assert.That(capturedBody).Contains("\"value\":1");
    }

    [Test]
    public async Task CourseApi_AdvanceActiveRoute_SurfacesServerFailure()
    {
        // 404 means no active route - caller should see false so the
        // "Next WP failed" toast fires instead of swallowing silently.
        var http = ApiTestHelpers.MockClient(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound));
        var api = new CourseApi(http, ApiTestHelpers.FixedBaseUrl(), NewNavPrefs());
        await Assert.That((await api.AdvanceActiveRouteAsync()).Success).IsFalse();
    }

    [Test]
    public async Task CourseApi_SetPointIndex_PutsAbsoluteIndex()
    {
        // SignalK v2 Course API: PUT /activeRoute/pointIndex with body
        // {"value": N} jumps to leg N. Used by the "tap a WP on the
        // route polyline to skip to it" gesture. Must hit the
        // dedicated endpoint, not /nextPoint (which expects a relative
        // delta) and not /activeRoute (which would re-resolve the href
        // and is heavier).
        string? capturedUrl = null;
        string? capturedBody = null;
        HttpMethod? capturedMethod = null;
        var http = ApiTestHelpers.MockClient(req =>
        {
            capturedUrl = req.RequestUri?.AbsoluteUri;
            capturedMethod = req.Method;
            capturedBody = req.Content?.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var api = new CourseApi(http, ApiTestHelpers.FixedBaseUrl(), NewNavPrefs());

        var r = await api.SetPointIndexAsync(3);

        await Assert.That(r.Success).IsTrue();
        await Assert.That(capturedMethod).IsEqualTo(HttpMethod.Put);
        await Assert.That(capturedUrl).EndsWith("/signalk/v2/api/vessels/self/navigation/course/activeRoute/pointIndex");
        await Assert.That(capturedBody).Contains("\"value\":3");
    }

    [Test]
    public async Task CourseApi_SetPointIndex_AcceptsZero()
    {
        // 0 is a legitimate target (jump back to the first leg) and
        // must not be coerced or treated as "no value sent". Pin it.
        string? capturedBody = null;
        var http = ApiTestHelpers.MockClient(req =>
        {
            capturedBody = req.Content?.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var api = new CourseApi(http, ApiTestHelpers.FixedBaseUrl(), NewNavPrefs());

        await api.SetPointIndexAsync(0);

        await Assert.That(capturedBody).Contains("\"value\":0");
    }

    [Test]
    public async Task CourseApi_SetPointIndex_SurfacesServerFailure()
    {
        var http = ApiTestHelpers.MockClient(_ =>
            new HttpResponseMessage(HttpStatusCode.BadRequest));
        var api = new CourseApi(http, ApiTestHelpers.FixedBaseUrl(), NewNavPrefs());
        await Assert.That((await api.SetPointIndexAsync(2)).Success).IsFalse();
    }

    [Test]
    public async Task CourseApi_Clear_DeletesCourse()
    {
        string? capturedUrl = null;
        HttpMethod? capturedMethod = null;
        var http = ApiTestHelpers.MockClient(req =>
        {
            capturedUrl = req.RequestUri?.AbsoluteUri;
            capturedMethod = req.Method;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var api = new CourseApi(http, ApiTestHelpers.FixedBaseUrl(), NewNavPrefs());

        var r = await api.ClearAsync();

        await Assert.That(r.Success).IsTrue();
        await Assert.That(capturedMethod).IsEqualTo(HttpMethod.Delete);
        await Assert.That(capturedUrl).EndsWith("/signalk/v2/api/vessels/self/navigation/course");
    }

    [Test]
    public async Task CourseApi_SetDestination_IncludesHelmConfiguredArrivalCircle()
    {
        // SignalK v2 Course API accepts an arrivalCircle field on the
        // destination / activeRoute PUT bodies. The server adopts it
        // as the effective navigation.course.arrivalCircle. This test
        // pins that the value flows from INavPreferences through to
        // the wire body for all three Set-* operations.
        string? capturedDest = null;
        string? capturedDestPos = null;
        string? capturedActiveRoute = null;
        var http = ApiTestHelpers.MockClient(req =>
        {
            var url = req.RequestUri?.AbsoluteUri ?? string.Empty;
            var body = req.Content?.ReadAsStringAsync().Result;
            if (url.EndsWith("/destination"))
            {
                if (capturedDest is null) capturedDest = body;
                else capturedDestPos = body;
            }
            else if (url.EndsWith("/activeRoute"))
            {
                capturedActiveRoute = body;
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var api = new CourseApi(http, ApiTestHelpers.FixedBaseUrl(),
            NewNavPrefs(arrivalCircleM: 75.0));

        await api.SetDestinationAsync("wpt-1");
        await api.SetDestinationPositionAsync(47.4, 8.5);
        await api.SetActiveRouteAsync("rte-1");

        await Assert.That(capturedDest).Contains("\"arrivalCircle\":75");
        await Assert.That(capturedDestPos).Contains("\"arrivalCircle\":75");
        await Assert.That(capturedActiveRoute).Contains("\"arrivalCircle\":75");
    }

    [Test]
    public async Task AutopilotApi_AdjustHeading_ConvertsDegreesToRadians()
    {
        string? capturedBody = null;
        var http = ApiTestHelpers.MockClient(req =>
        {
            capturedBody = req.Content?.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var api = new AutopilotApi(http, ApiTestHelpers.FixedBaseUrl());

        await api.AdjustHeadingAsync(10);

        // 10 degrees = 0.17453292519943295 radians (approx).
        await Assert.That(capturedBody).Contains("0.17453");
    }

    [Test]
    public async Task AutopilotApi_SetState_SendsValue()
    {
        string? capturedBody = null;
        var http = ApiTestHelpers.MockClient(req =>
        {
            capturedBody = req.Content?.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var api = new AutopilotApi(http, ApiTestHelpers.FixedBaseUrl());

        await api.SetStateAsync("auto");

        await Assert.That(capturedBody).Contains("\"value\":\"auto\"");
    }

    [Test]
    public async Task PathApi_FlattensNavPaths()
    {
        var body = """
        {
            "navigation": {
                "speedOverGround": { "value": 5.0 },
                "position": { "value": {} }
            }
        }
        """;
        var http = ApiTestHelpers.JsonClient(_ => body);
        var api = new PathApi(http, ApiTestHelpers.FixedBaseUrl());

        var paths = await api.GetAvailablePathsAsync();

        await Assert.That(paths).Contains("navigation.position");
        await Assert.That(paths).Contains("navigation.speedOverGround");
    }
}
