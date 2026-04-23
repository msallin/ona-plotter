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
    public async Task SaveAsync_PostsToRoutesEndpoint_AndReturnsId_StructuredResponse()
    {
        // signalk-server 2.x returns the new-id inside a structured
        // envelope. Pin that we parse it correctly AND expose the
        // id on ApiResult<string>.Value so the caller can activate
        // the route without refetching the resource list.
        string? capturedUrl = null;
        HttpMethod? capturedMethod = null;
        var http = ApiTestHelpers.MockClient(req =>
        {
            capturedUrl = req.RequestUri?.AbsoluteUri;
            capturedMethod = req.Method;
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(
                    """{"state":"COMPLETED","statusCode":201,"id":"abc-123"}""",
                    System.Text.Encoding.UTF8, "application/json"),
            };
        });
        var api = new RouteApi(http, ApiTestHelpers.FixedBaseUrl());

        var r = await api.SaveAsync("My Route", [[47.0, 8.0], [48.0, 9.0]]);

        await Assert.That(r.Success).IsTrue();
        await Assert.That(r.Value).IsEqualTo("abc-123");
        await Assert.That(capturedMethod).IsEqualTo(HttpMethod.Post);
        await Assert.That(capturedUrl).IsEqualTo($"{ApiTestHelpers.TestBase}/signalk/v2/api/resources/routes");
    }

    [Test]
    public async Task SaveAsync_AcceptsBareStringResponse_LegacyServers()
    {
        // Older signalk-server builds return just the UUID as a
        // bare JSON string. PostCreateAsync -> ParseCreatedId still
        // unwraps that shape.
        var http = ApiTestHelpers.MockClient(_ =>
            new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("\"legacy-uuid\"",
                    System.Text.Encoding.UTF8, "application/json"),
            });
        var api = new RouteApi(http, ApiTestHelpers.FixedBaseUrl());

        var r = await api.SaveAsync("My Route", [[47.0, 8.0], [48.0, 9.0]]);

        await Assert.That(r.Success).IsTrue();
        await Assert.That(r.Value).IsEqualTo("legacy-uuid");
    }

    [Test]
    public async Task SaveAsync_Fails_WhenServerReturnsNoId()
    {
        // 2xx with an empty body means the server accepted the
        // create but didn't surface an id -- the caller has nothing
        // to activate, so treat this as a failure.
        var http = ApiTestHelpers.MockClient(_ =>
            new HttpResponseMessage(HttpStatusCode.Created));
        var api = new RouteApi(http, ApiTestHelpers.FixedBaseUrl());

        var r = await api.SaveAsync("My Route", [[47.0, 8.0], [48.0, 9.0]]);
        await Assert.That(r.Success).IsFalse();
    }

    [Test]
    public async Task SaveAsync_EmitsCompleteGeoJsonFeature()
    {
        // GeoJSON requires `properties` on every Feature, and freeboard-sk
        // expects coordinatesMeta one-per-waypoint. Guard the payload shape.
        string body = string.Empty;
        var http = ApiTestHelpers.MockClient(req =>
        {
            body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.Created);
        });
        var api = new RouteApi(http, ApiTestHelpers.FixedBaseUrl());

        await api.SaveAsync("Test Route", [[47.0, 8.0], [48.0, 9.0], [49.0, 10.0]]);

        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var root = doc.RootElement;

        // Top-level SignalK route name.
        await Assert.That(root.GetProperty("name").GetString()).IsEqualTo("Test Route");

        // feature.properties must always be present (GeoJSON spec).
        var feature = root.GetProperty("feature");
        await Assert.That(feature.GetProperty("type").GetString()).IsEqualTo("Feature");

        var props = feature.GetProperty("properties");
        await Assert.That(props.ValueKind).IsEqualTo(System.Text.Json.JsonValueKind.Object);
        await Assert.That(props.GetProperty("name").GetString()).IsEqualTo("Test Route");
        await Assert.That(props.GetProperty("description").GetString()).IsEqualTo("");

        // One coordinatesMeta entry per waypoint.
        var meta = props.GetProperty("coordinatesMeta");
        await Assert.That(meta.GetArrayLength()).IsEqualTo(3);

        // Coordinates flipped to GeoJSON [lon, lat] order.
        var coords = feature.GetProperty("geometry").GetProperty("coordinates");
        await Assert.That(coords[0][0].GetDouble()).IsEqualTo(8.0);
        await Assert.That(coords[0][1].GetDouble()).IsEqualTo(47.0);
    }
}
