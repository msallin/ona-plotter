using System.Net;
using System.Text.Json;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Tests;

public class RegionApiTests
{
    [Test]
    public async Task CreateCircle_EmitsPolygonGeometryWithProperties()
    {
        // Pins the wire shape so the region lands correctly in
        // Freeboard-SK: GeoJSON Feature -> Polygon geometry with an
        // outer ring of [lon, lat] pairs, plus a properties block that
        // mirrors the top-level name/description (the route fix
        // 1ca24fc pattern).
        string? capturedBody = null;
        var http = ApiTestHelpers.MockClient(req =>
        {
            capturedBody = req.Content?.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("\"rgn-new-1\""),
            };
        });
        var api = new RegionApi(http, ApiTestHelpers.FixedBaseUrl());

        var id = await api.CreateCircleAsync("Anchorage", "Holds well", 47.4, 8.5, 300);

        await Assert.That(id).IsEqualTo("rgn-new-1");
        await Assert.That(capturedBody).IsNotNull();
        await Assert.That(capturedBody).Contains("\"name\":\"Anchorage\"");
        await Assert.That(capturedBody).Contains("\"description\":\"Holds well\"");
        await Assert.That(capturedBody).Contains("\"type\":\"Feature\"");
        await Assert.That(capturedBody).Contains("\"type\":\"Polygon\"");
        await Assert.That(capturedBody).Contains("\"properties\"");
    }

    [Test]
    public async Task BuildCircleRing_ProducesClosedApproximation()
    {
        // The ring must be closed (first == last) and every vertex
        // should be within one-percent of the requested radius. Checks
        // the equirectangular approximation we use doesn't distort the
        // circle at a reasonable latitude.
        double lat = 47.4, lon = 8.5, radius = 500;
        var ring = RegionApi.BuildCircleRing(lat, lon, radius, 32);

        await Assert.That(ring.Length).IsEqualTo(33); // 32 vertices + closing point
        await Assert.That(ring[0][0]).IsEqualTo(ring[^1][0]); // same lon
        await Assert.That(ring[0][1]).IsEqualTo(ring[^1][1]); // same lat

        // GeoJSON convention: [lon, lat]. Every vertex should sit
        // roughly `radius` metres from the centre.
        foreach (var v in ring)
        {
            double vLon = v[0], vLat = v[1];
            double meters = HaversineMeters(lat, lon, vLat, vLon);
            await Assert.That(Math.Abs(meters - radius) < radius * 0.02).IsTrue();
        }
    }

    [Test]
    public async Task GetAll_ParsesPolygonCoordinates()
    {
        // Polygon coordinates nested three deep: [ring[ [lon, lat], ...] ]
        var body = """
        {
            "rgn-1": {
                "name": "Kelp",
                "description": "watch the prop",
                "feature": {
                    "type": "Feature",
                    "geometry": {
                        "type": "Polygon",
                        "coordinates": [[[8.5, 47.4], [8.6, 47.4], [8.6, 47.5], [8.5, 47.5], [8.5, 47.4]]]
                    }
                }
            }
        }
        """;
        var http = ApiTestHelpers.JsonClient(_ => body);
        var api = new RegionApi(http, ApiTestHelpers.FixedBaseUrl());

        var regions = await api.GetAllAsync();

        await Assert.That(regions.Count).IsEqualTo(1);
        var r = regions[0];
        await Assert.That(r.Id).IsEqualTo("rgn-1");
        await Assert.That(r.Name).IsEqualTo("Kelp");
        await Assert.That(r.OuterRings.Count).IsEqualTo(1);

        // Coordinates should have been swapped from [lon, lat] to [lat, lon].
        var ring = r.OuterRings[0];
        await Assert.That(ring.Length).IsEqualTo(5);
        await Assert.That(ring[0][0]).IsEqualTo(47.4); // lat first in Leaflet order
        await Assert.That(ring[0][1]).IsEqualTo(8.5);
    }

    [Test]
    public async Task GetAll_ParsesMultiPolygonOuterRings()
    {
        // MultiPolygon: list of polygons, each with its own outer+inner rings.
        var body = """
        {
            "rgn-mp": {
                "name": "Two islets",
                "feature": {
                    "type": "Feature",
                    "geometry": {
                        "type": "MultiPolygon",
                        "coordinates": [
                            [[[8.5, 47.4], [8.6, 47.4], [8.55, 47.5], [8.5, 47.4]]],
                            [[[9.0, 47.4], [9.1, 47.4], [9.05, 47.5], [9.0, 47.4]]]
                        ]
                    }
                }
            }
        }
        """;
        var http = ApiTestHelpers.JsonClient(_ => body);
        var api = new RegionApi(http, ApiTestHelpers.FixedBaseUrl());

        var regions = await api.GetAllAsync();

        await Assert.That(regions.Count).IsEqualTo(1);
        // Both outer rings extracted; holes ignored.
        await Assert.That(regions[0].OuterRings.Count).IsEqualTo(2);
    }

    [Test]
    public async Task GetAll_DropsRegionsWithoutRenderableGeometry()
    {
        // Point geometry, missing feature, and a degenerate ring all
        // get filtered so the Map layer doesn't have to special-case
        // empty rings.
        var body = """
        {
            "rgn-bad-point": {
                "name": "Not a region",
                "feature": { "type": "Feature",
                             "geometry": { "type": "Point", "coordinates": [8.5, 47.4] } }
            },
            "rgn-no-feature": { "name": "Nothing" },
            "rgn-degenerate": {
                "feature": { "type": "Feature",
                             "geometry": { "type": "Polygon", "coordinates": [[[8.5, 47.4]]] } }
            }
        }
        """;
        var http = ApiTestHelpers.JsonClient(_ => body);
        var api = new RegionApi(http, ApiTestHelpers.FixedBaseUrl());

        var regions = await api.GetAllAsync();
        await Assert.That(regions.Count).IsEqualTo(0);
    }

    [Test]
    public async Task CreatePolygon_Closes_Ring_And_Swaps_To_GeoJson_Order()
    {
        // Freeform polygon: C# side sends [lat, lon] Leaflet order, API
        // has to emit GeoJSON [lon, lat] order and auto-close by repeating
        // the first vertex. Freeboard-SK consumers assume a closed ring.
        string? capturedBody = null;
        var http = ApiTestHelpers.MockClient(req =>
        {
            capturedBody = req.Content?.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("\"rgn-poly-1\""),
            };
        });
        var api = new RegionApi(http, ApiTestHelpers.FixedBaseUrl());

        var vertices = new[]
        {
            new[] { 47.40, 8.50 },
            new[] { 47.41, 8.51 },
            new[] { 47.42, 8.50 },
        };
        var id = await api.CreatePolygonAsync("Triangle", "T-test", vertices);

        await Assert.That(id).IsEqualTo("rgn-poly-1");
        await Assert.That(capturedBody).IsNotNull();
        // GeoJSON [lon, lat] ordering (first vertex).
        await Assert.That(capturedBody).Contains("[8.5,47.4]");
        // Ring must be closed; first vertex repeats at the end. The JSON
        // serializer emits the ring inline so we can find the closing
        // pair after the last distinct vertex.
        int firstIdx = capturedBody!.IndexOf("[8.5,47.4]");
        int lastIdx = capturedBody.LastIndexOf("[8.5,47.4]");
        await Assert.That(lastIdx).IsGreaterThan(firstIdx);
        await Assert.That(capturedBody).Contains("\"type\":\"Polygon\"");
        await Assert.That(capturedBody).Contains("\"name\":\"Triangle\"");
    }

    [Test]
    public async Task CreatePolygon_Rejects_Fewer_Than_Three_Vertices()
    {
        // A single-vertex "polygon" would round-trip as a degenerate ring
        // that later GetAllAsync would drop anyway; fail fast here.
        var http = ApiTestHelpers.MockClient(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("\"never\"") });
        var api = new RegionApi(http, ApiTestHelpers.FixedBaseUrl());

        var two = new[] { new[] { 47.4, 8.5 }, new[] { 47.5, 8.5 } };
        await Assert.That(await api.CreatePolygonAsync("Too short", "", two)).IsNull();

        await Assert.That(await api.CreatePolygonAsync("Empty", "", System.Array.Empty<double[]>())).IsNull();

        await Assert.That(await api.CreatePolygonAsync("Null", "", null!)).IsNull();
    }

    [Test]
    public async Task CreatePolygon_Rejects_Bad_Vertex_Shape()
    {
        // A vertex with only one component shouldn't crash the serializer
        // mid-request; reject before we send.
        var http = ApiTestHelpers.MockClient(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("\"never\"") });
        var api = new RegionApi(http, ApiTestHelpers.FixedBaseUrl());

        var bad = new[]
        {
            new[] { 47.4, 8.5 },
            new[] { 47.5 },          // only lat; no lon
            new[] { 47.6, 8.6 },
        };
        await Assert.That(await api.CreatePolygonAsync("Bad", "", bad)).IsNull();
    }

    [Test]
    public async Task Delete_UrlEncodesId()
    {
        string? capturedUrl = null;
        HttpMethod? capturedMethod = null;
        var http = ApiTestHelpers.MockClient(req =>
        {
            capturedMethod = req.Method;
            capturedUrl = req.RequestUri?.AbsoluteUri;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var api = new RegionApi(http, ApiTestHelpers.FixedBaseUrl());

        var ok = await api.DeleteAsync("id with space");

        await Assert.That(ok).IsTrue();
        await Assert.That(capturedMethod).IsEqualTo(HttpMethod.Delete);
        await Assert.That(capturedUrl).EndsWith("/regions/id%20with%20space");
    }

    private static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6_371_000;
        double toRad = Math.PI / 180.0;
        double dLat = (lat2 - lat1) * toRad;
        double dLon = (lon2 - lon1) * toRad;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                 + Math.Cos(lat1 * toRad) * Math.Cos(lat2 * toRad) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}
