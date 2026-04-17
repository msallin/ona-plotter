using System.Net;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Tests;

public class WaypointCourseApiTests
{
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
        var api = new CourseApi(http, ApiTestHelpers.FixedBaseUrl());

        var ok = await api.SetDestinationAsync("wpt-42");

        await Assert.That(ok).IsTrue();
        await Assert.That(capturedMethod).IsEqualTo(HttpMethod.Put);
        await Assert.That(capturedBody).Contains("/resources/waypoints/wpt-42");
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
        var api = new CourseApi(http, ApiTestHelpers.FixedBaseUrl());

        var ok = await api.ClearAsync();

        await Assert.That(ok).IsTrue();
        await Assert.That(capturedMethod).IsEqualTo(HttpMethod.Delete);
        await Assert.That(capturedUrl).EndsWith("/signalk/v2/api/navigation/course");
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
