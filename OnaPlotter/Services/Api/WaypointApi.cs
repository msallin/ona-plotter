using System.Net.Http.Json;
using OnaPlotter.Models;
using OnaPlotter.Utilities;

namespace OnaPlotter.Services.Api;

public sealed class WaypointApi : IWaypointApi
{
    private readonly HttpClient _http;
    private readonly ISignalKBaseUrl _baseUrl;

    public WaypointApi(HttpClient http, ISignalKBaseUrl baseUrl)
    {
        _http = http;
        _baseUrl = baseUrl;
    }

    public async Task<List<SignalkWaypoint>> GetAllAsync(CancellationToken ct = default)
    {
        var dict = await ResourceHttp.GetDictAsync<SignalkWaypoint>(
            _http, _baseUrl.Combine(SignalKUrls.WaypointsPath), ct);
        if (dict is null) return [];

        var waypoints = new List<SignalkWaypoint>(dict.Count);
        foreach (var (key, wp) in dict)
        {
            wp.Id = key;
            var coords = wp.Feature?.Geometry?.Coordinates;
            if (coords is not null)
            {
                var latLon = coords.Value.ToLatLonPoint();
                if (latLon is not null)
                {
                    wp.Latitude = latLon.Value.Latitude;
                    wp.Longitude = latLon.Value.Longitude;
                }
            }
            waypoints.Add(wp);
        }
        return waypoints;
    }

    public async Task<string?> CreateAsync(string name, double lat, double lon,
        string? description = null, CancellationToken ct = default)
    {
        // GeoJSON requires `properties` on every Feature (empty is fine)
        // and freeboard-sk explicitly assumes it's always present.
        // Shipping just `{type, geometry}` produced waypoints that
        // displayed broken or not at all when the same SignalK server
        // was viewed through freeboard. Mirrors the route fix
        // (1ca24fc): top-level `name` stays as the SignalK resource
        // name; properties.name/description are the per-GeoJSON copy so
        // any consumer picks them up.
        var body = new
        {
            name,
            feature = new
            {
                type = "Feature",
                geometry = new { type = "Point", coordinates = new[] { lon, lat } },
                properties = new
                {
                    name,
                    description = description ?? "",
                }
            }
        };
        var url = _baseUrl.Combine(SignalKUrls.WaypointsPath);
        using var response = await _http.PostAsJsonAsync(url, body, ct);
        if (!response.IsSuccessStatusCode) return null;

        var result = await response.Content.ReadAsStringAsync(ct);
        return ResourceHttp.ParseCreatedId(result);
    }

    public Task<bool> DeleteAsync(string id, CancellationToken ct = default) =>
        ResourceHttp.DeleteAsync(_http, _baseUrl.Combine(SignalKUrls.Waypoint(id)), ct);
}
