using System.Net.Http.Json;
using System.Text.Json;
using OnaPlotter.Models;
using OnaPlotter.Utilities;

namespace OnaPlotter.Services.Api;

public sealed class RouteApi : IRouteApi
{
    private readonly HttpClient _http;
    private readonly ISignalKBaseUrl _baseUrl;

    public RouteApi(HttpClient http, ISignalKBaseUrl baseUrl)
    {
        _http = http;
        _baseUrl = baseUrl;
    }

    public async Task<List<SignalkRoute>> GetAllAsync(CancellationToken ct = default)
    {
        var dict = await ResourceHttp.GetDictAsync<SignalkRoute>(
            _http, _baseUrl.Combine(SignalKUrls.RoutesPath), ct);
        if (dict is null) return [];

        var routes = new List<SignalkRoute>(dict.Count);
        foreach (var (key, route) in dict)
        {
            route.Id = key;
            if (route.Feature?.Geometry?.Type is "LineString")
                routes.Add(route);
        }
        return routes;
    }

    public async Task<double[][]?> GetCoordinatesAsync(string href, CancellationToken ct = default)
    {
        var routeId = SignalKUrls.ExtractRouteId(href);
        var url = _baseUrl.Combine(SignalKUrls.Route(routeId));
        using var response = await _http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return null;

        var route = await response.Content.ReadFromJsonAsync<SignalkRoute>(cancellationToken: ct);
        var coords = route?.Feature?.Geometry?.Coordinates;
        if (coords is null || coords.Value.ValueKind != JsonValueKind.Array) return null;

        return coords.Value.ToLeafletLineString();
    }

    public Task<ApiResult<string>> SaveAsync(string name, double[][] coordsLatLon, CancellationToken ct = default)
    {
        // POST /resources/routes -> new id. Returns the server-
        // generated uuid in Value so the caller can activate /
        // render the route without reloading the full list. Before
        // this was switched to PostCreateAsync the caller had to
        // refetch /resources/routes and DIFF against the prior id
        // set to find the fresh route, which was (a) racy against
        // the same server's next broadcast and (b) quietly wrong in
        // multi-plotter setups where another client might have
        // added its own route in the same interval.
        var body = BuildRouteBody(name, coordsLatLon);
        var url = _baseUrl.Combine(SignalKUrls.RoutesPath);
        return ResourceHttp.PostCreateAsync(_http, url, body, ct);
    }

    public Task<ApiResult> UpdateAsync(string id, string name, double[][] coordsLatLon, CancellationToken ct = default)
    {
        // PUT /resources/routes/{id} -> rewrite in place. Used when
        // the edit session was started from an existing route so the
        // helm's tweaks replace the original rather than spawning a
        // second copy on every save.
        if (string.IsNullOrEmpty(id)) return Task.FromResult(ApiResult.Fail("route id required"));
        var body = BuildRouteBody(name, coordsLatLon);
        var url = _baseUrl.Combine(SignalKUrls.Route(id));
        return ResourceHttp.PutAsync(_http, url, body, ct);
    }

    public Task<ApiResult> DeleteAsync(string id, CancellationToken ct = default) =>
        ResourceHttp.DeleteAsync(_http, _baseUrl.Combine(SignalKUrls.Route(id)), ct);

    // --- shared body shape + transport helpers ----------------------

    private static object BuildRouteBody(string name, double[][] coordsLatLon) =>
        GeoJsonBuilder.RouteFeatureBody(
            name,
            GeoJsonBuilder.LineString(coordsLatLon),
            waypointCount: coordsLatLon.Length,
            // Total leg-by-leg distance in metres along the route's
            // LineString. Without this field SignalkRoute.Distance
            // round-trips as null and the Layers panel + Resources
            // page show "-" for OnaPlotter-created routes (a route
            // created by another SignalK client on the same server
            // shows the value because peer clients always emit this
            // property).
            distanceMeters: coordsLatLon.Length >= 2
                ? OnaPlotter.Utilities.RouteProgress.TotalDistanceMeters(coordsLatLon)
                : (double?)null);
}
