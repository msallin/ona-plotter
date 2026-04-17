using System.Net;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Tests;

public class RouteApiTests
{
    [Test]
    public async Task GetAll_EmptyDictReturnsEmptyList()
    {
        var http = ApiTestHelpers.JsonClient(_ => "{}");
        var api = new RouteApi(http, ApiTestHelpers.FixedBaseUrl());

        var routes = await api.GetAllAsync();
        await Assert.That(routes.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetAll_FiltersNonLineStringGeometry()
    {
        var body = """
        {
            "r1": {
                "name": "Route 1",
                "feature": { "type": "Feature", "geometry": { "type": "LineString", "coordinates": [[8,47],[9,48]] } }
            },
            "r2": {
                "name": "Point",
                "feature": { "type": "Feature", "geometry": { "type": "Point", "coordinates": [8,47] } }
            }
        }
        """;
        var http = ApiTestHelpers.JsonClient(_ => body);
        var api = new RouteApi(http, ApiTestHelpers.FixedBaseUrl());

        var routes = await api.GetAllAsync();
        await Assert.That(routes.Count).IsEqualTo(1);
        await Assert.That(routes[0].Id).IsEqualTo("r1");
        await Assert.That(routes[0].Name).IsEqualTo("Route 1");
    }

    [Test]
    public async Task GetAll_FailureReturnsEmpty()
    {
        var http = ApiTestHelpers.JsonClient(_ => "", HttpStatusCode.ServiceUnavailable);
        var api = new RouteApi(http, ApiTestHelpers.FixedBaseUrl());

        var routes = await api.GetAllAsync();
        await Assert.That(routes.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetCoordinates_ReturnsLatLonPairs()
    {
        var body = """
        { "name": "Route", "feature": { "type": "Feature",
          "geometry": { "type": "LineString", "coordinates": [[8.5,47.4],[9.0,48.0]] } } }
        """;
        var http = ApiTestHelpers.JsonClient(_ => body);
        var api = new RouteApi(http, ApiTestHelpers.FixedBaseUrl());

        var coords = await api.GetCoordinatesAsync("/resources/routes/abc");

        await Assert.That(coords).IsNotNull();
        await Assert.That(coords!.Length).IsEqualTo(2);
        await Assert.That(coords[0][0]).IsEqualTo(47.4);
        await Assert.That(coords[0][1]).IsEqualTo(8.5);
    }

    [Test]
    public async Task GetCoordinates_UsesCorrectUrl()
    {
        string? capturedUrl = null;
        var http = ApiTestHelpers.MockClient(req =>
        {
            capturedUrl = req.RequestUri?.AbsoluteUri;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"feature":{"geometry":{"coordinates":[]}}}""",
                    System.Text.Encoding.UTF8, "application/json")
            };
        });
        var api = new RouteApi(http, ApiTestHelpers.FixedBaseUrl());

        await api.GetCoordinatesAsync("abc/xyz");

        // ID is URL-encoded and nested under the v2 path.
        await Assert.That(capturedUrl).IsEqualTo($"{ApiTestHelpers.TestBase}/signalk/v2/api/resources/routes/abc%2Fxyz");
    }

    [Test]
    public async Task SaveAsync_PostsToRoutesEndpoint()
    {
        string? capturedUrl = null;
        HttpMethod? capturedMethod = null;
        var http = ApiTestHelpers.MockClient(req =>
        {
            capturedUrl = req.RequestUri?.AbsoluteUri;
            capturedMethod = req.Method;
            return new HttpResponseMessage(HttpStatusCode.Created);
        });
        var api = new RouteApi(http, ApiTestHelpers.FixedBaseUrl());

        var ok = await api.SaveAsync("My Route", [[47.0, 8.0], [48.0, 9.0]]);

        await Assert.That(ok).IsTrue();
        await Assert.That(capturedMethod).IsEqualTo(HttpMethod.Post);
        await Assert.That(capturedUrl).IsEqualTo($"{ApiTestHelpers.TestBase}/signalk/v2/api/resources/routes");
    }
}
