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
        var url = _baseUrl.Combine(SignalKUrls.RoutesPath);
        using var response = await _http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return [];

        var dict = await response.Content.ReadFromJsonAsync<Dictionary<string, SignalkRoute>>(cancellationToken: ct);
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

    public async Task<bool> SaveAsync(string name, double[][] coordsLatLon, CancellationToken ct = default)
    {
        var geoJsonCoords = coordsLatLon.Select(c => new[] { c[1], c[0] }).ToArray();
        var body = new
        {
            name,
            feature = new
            {
                type = "Feature",
                geometry = new { type = "LineString", coordinates = geoJsonCoords }
            }
        };
        var url = _baseUrl.Combine(SignalKUrls.RoutesPath);
        using var response = await _http.PostAsJsonAsync(url, body, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        var url = _baseUrl.Combine(SignalKUrls.Route(id));
        using var response = await _http.DeleteAsync(url, ct);
        return response.IsSuccessStatusCode;
    }
}
