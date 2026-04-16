// Fetches available chart layers from the SignalK server's charts API.

using System.Net.Http.Json;
using System.Text.Json;
using OnaPlotter.Models;

namespace OnaPlotter.Services;

/// <summary>
/// HTTP client for the SignalK REST API. Fetches available chart layers, saved
/// routes, server-side position track history, and discovers available data paths.
/// </summary>
public sealed class ChartService
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly ILogger<ChartService> _logger;

    public ChartService(IConfiguration configuration, HttpClient http, ILogger<ChartService> logger)
    {
        _http = http;
        _logger = logger;

        _baseUrl = configuration["SignalK:ServerUrl"]
            ?? throw new InvalidOperationException("SignalK:ServerUrl is not configured.");
        _baseUrl = _baseUrl.TrimEnd('/');
    }

    /// <summary>
    /// Fetches the chart list from /signalk/v1/api/resources/charts.
    /// Returns charts that have a usable tile URL.
    /// </summary>
    public async Task<List<SignalkChart>> GetChartsAsync()
    {
        try
        {
            var url = $"{_baseUrl}/signalk/v1/api/resources/charts";
            var response = await _http.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Charts API returned {Status}", response.StatusCode);
                return [];
            }

            var json = await response.Content.ReadAsStringAsync();
            var dict = JsonSerializer.Deserialize<Dictionary<string, SignalkChart>>(json);

            if (dict is null) return [];

            var charts = new List<SignalkChart>();
            foreach (var (key, chart) in dict)
            {
                if (string.IsNullOrEmpty(chart.Identifier))
                    chart.Identifier = key;

                // Resolve relative tile URLs to absolute.
                var tileUrl = chart.GetTileUrl();
                if (tileUrl is not null && tileUrl.StartsWith('/'))
                {
                    chart.TilemapUrl = _baseUrl + tileUrl;
                }

                if (chart.GetTileUrl() is not null)
                    charts.Add(chart);
            }

            _logger.LogInformation("Loaded {Count} charts from SignalK", charts.Count);
            return charts;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch charts from SignalK");
            return [];
        }
    }

    /// <summary>
    /// Fetches saved routes from /signalk/v2/api/resources/routes.
    /// </summary>
    public async Task<List<SignalkRoute>> GetRoutesAsync()
    {
        try
        {
            var url = $"{_baseUrl}/signalk/v2/api/resources/routes";
            var response = await _http.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Routes API returned {Status}", response.StatusCode);
                return [];
            }

            var json = await response.Content.ReadAsStringAsync();
            var dict = JsonSerializer.Deserialize<Dictionary<string, SignalkRoute>>(json);

            if (dict is null) return [];

            var routes = new List<SignalkRoute>();
            foreach (var (key, route) in dict)
            {
                route.Id = key;
                if (route.Feature?.Geometry?.Type is "LineString")
                    routes.Add(route);
            }

            _logger.LogInformation("Loaded {Count} routes from SignalK", routes.Count);
            return routes;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch routes from SignalK");
            return [];
        }
    }

    /// <summary>
    /// Fetches the server-side vessel track from /signalk/v1/api/self/track.
    /// Returns coordinate arrays as [lat, lon] pairs (converted from GeoJSON [lon, lat]).
    /// </summary>
    public async Task<double[][]?> GetServerTrackAsync(string timespan = "1d", string resolution = "1m")
    {
        try
        {
            var url = $"{_baseUrl}/signalk/v1/api/self/track?timespan={timespan}&resolution={resolution}";
            var response = await _http.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Track API returned {Status}", response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Response is a GeoJSON geometry (MultiLineString).
            // { "type": "MultiLineString", "coordinates": [[[lon,lat], ...], ...] }
            if (!root.TryGetProperty("coordinates", out var coords))
                return null;

            var points = new List<double[]>();
            foreach (var line in coords.EnumerateArray())
            {
                foreach (var point in line.EnumerateArray())
                {
                    var arr = new double[2];
                    int i = 0;
                    foreach (var val in point.EnumerateArray())
                    {
                        if (i < 2) arr[i++] = val.GetDouble();
                    }
                    // GeoJSON is [lon, lat]; Leaflet needs [lat, lon].
                    points.Add([arr[1], arr[0]]);
                }
            }

            _logger.LogInformation("Loaded server track with {Count} points", points.Count);
            return [.. points];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch server track from SignalK");
            return null;
        }
    }

    /// <summary>
    /// Fetches route coordinates by the activeRoute href from SignalK.
    /// The href is typically "/resources/routes/{uuid}" or just a UUID.
    /// Returns [lat, lon] pairs suitable for Leaflet, or null on failure.
    /// </summary>
    public async Task<double[][]?> GetRouteCoordinatesAsync(string href)
    {
        try
        {
            // Extract route UUID from href.
            // Formats: "/resources/routes/{uuid}", "/signalk/v2/api/resources/routes/{uuid}", or just "{uuid}".
            string routeId = href;
            const string marker = "/resources/routes/";
            int idx = href.LastIndexOf(marker, StringComparison.Ordinal);
            if (idx >= 0)
                routeId = href[(idx + marker.Length)..];

            var url = $"{_baseUrl}/signalk/v2/api/resources/routes/{routeId}";
            var response = await _http.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Route fetch for {Href} returned {Status}", href, response.StatusCode);
                return null;
            }

            var route = await response.Content.ReadFromJsonAsync<SignalkRoute>();
            if (route?.Feature?.Geometry?.Coordinates.ValueKind != JsonValueKind.Array)
                return null;

            var coords = new List<double[]>();
            foreach (var point in route.Feature.Geometry.Coordinates.EnumerateArray())
            {
                var arr = new double[2];
                int i = 0;
                foreach (var val in point.EnumerateArray())
                {
                    if (i < 2) arr[i++] = val.GetDouble();
                }
                coords.Add([arr[1], arr[0]]); // GeoJSON [lon, lat] -> Leaflet [lat, lon]
            }

            _logger.LogInformation("Fetched active route with {Count} waypoints", coords.Count);
            return [.. coords];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch route for href {Href}", href);
            return null;
        }
    }

    /// <summary>
    /// Fetches all available SignalK paths for the own vessel from the REST API.
    /// Walks the JSON tree from /signalk/v1/api/vessels/self and returns dotted path
    /// strings for every leaf that has a "value" property.
    /// Example: "navigation.speedOverGround", "environment.depth.belowTransducer".
    /// </summary>
    public async Task<List<string>> GetAvailablePathsAsync()
    {
        try
        {
            var url = $"{_baseUrl}/signalk/v1/api/vessels/self";
            var response = await _http.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Paths API returned {Status}", response.StatusCode);
                return [];
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var paths = new List<string>();
            FlattenPaths(doc.RootElement, "", paths);
            paths.Sort(StringComparer.OrdinalIgnoreCase);
            return paths;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch available paths from SignalK");
            return [];
        }
    }

    private static void FlattenPaths(JsonElement element, string prefix, List<string> paths)
    {
        if (element.ValueKind != JsonValueKind.Object) return;

        foreach (var prop in element.EnumerateObject())
        {
            var path = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}.{prop.Name}";

            if (prop.Value.ValueKind == JsonValueKind.Object
                && prop.Value.TryGetProperty("value", out _))
            {
                paths.Add(path);
            }
            else if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                FlattenPaths(prop.Value, path, paths);
            }
        }
    }

    // --- Route CRUD ---

    public async Task<bool> SaveRouteAsync(string name, double[][] coordsLatLon)
    {
        try
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
            var url = $"{_baseUrl}/signalk/v2/api/resources/routes";
            var response = await _http.PostAsJsonAsync(url, body);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Route save returned {Status}", response.StatusCode);
                return false;
            }
            _logger.LogInformation("Saved route '{Name}' with {Count} waypoints", name, coordsLatLon.Length);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save route '{Name}'", name);
            return false;
        }
    }

    public async Task<bool> DeleteRouteAsync(string id)
    {
        try
        {
            var response = await _http.DeleteAsync($"{_baseUrl}/signalk/v2/api/resources/routes/{Uri.EscapeDataString(id)}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete route {Id}", id);
            return false;
        }
    }

    // --- Waypoint CRUD ---

    public async Task<List<SignalkWaypoint>> GetWaypointsAsync()
    {
        try
        {
            var url = $"{_baseUrl}/signalk/v2/api/resources/waypoints";
            var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode) return [];

            var json = await response.Content.ReadAsStringAsync();
            var dict = JsonSerializer.Deserialize<Dictionary<string, SignalkWaypoint>>(json);
            if (dict is null) return [];

            var waypoints = new List<SignalkWaypoint>();
            foreach (var (key, wp) in dict)
            {
                wp.Id = key;
                if (wp.Feature?.Geometry?.Coordinates.ValueKind == JsonValueKind.Array)
                {
                    var arr = new double[2];
                    int i = 0;
                    foreach (var val in wp.Feature.Geometry.Coordinates.EnumerateArray())
                    {
                        if (i < 2) arr[i++] = val.GetDouble();
                    }
                    wp.Longitude = arr[0];
                    wp.Latitude = arr[1];
                }
                waypoints.Add(wp);
            }
            return waypoints;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch waypoints");
            return [];
        }
    }

    public async Task<string?> CreateWaypointAsync(string name, double lat, double lon)
    {
        try
        {
            var body = new
            {
                name,
                feature = new
                {
                    type = "Feature",
                    geometry = new { type = "Point", coordinates = new[] { lon, lat } }
                }
            };
            var url = $"{_baseUrl}/signalk/v2/api/resources/waypoints";
            var response = await _http.PostAsJsonAsync(url, body);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Waypoint create returned {Status}", response.StatusCode);
                return null;
            }
            var result = await response.Content.ReadAsStringAsync();
            return result.Trim('"');
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create waypoint '{Name}'", name);
            return null;
        }
    }

    public async Task<bool> DeleteWaypointAsync(string id)
    {
        try
        {
            var response = await _http.DeleteAsync($"{_baseUrl}/signalk/v2/api/resources/waypoints/{Uri.EscapeDataString(id)}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete waypoint {Id}", id);
            return false;
        }
    }

    // --- Course Navigation API ---

    public async Task<bool> SetCourseDestinationAsync(string waypointId)
    {
        try
        {
            var body = new { href = $"/resources/waypoints/{waypointId}" };
            var url = $"{_baseUrl}/signalk/v2/api/navigation/course/destination";
            var response = await _http.PutAsJsonAsync(url, body);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Set course destination returned {Status}", response.StatusCode);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set course destination");
            return false;
        }
    }

    public async Task<bool> ClearCourseAsync()
    {
        try
        {
            var response = await _http.DeleteAsync($"{_baseUrl}/signalk/v2/api/navigation/course");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear course");
            return false;
        }
    }

    // --- Autopilot API ---

    public async Task<bool> SetAutopilotStateAsync(string state)
    {
        try
        {
            var body = new { value = state };
            var url = $"{_baseUrl}/signalk/v2/api/vessels/self/steering/autopilot/state";
            var response = await _http.PutAsJsonAsync(url, body);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set autopilot state to {State}", state);
            return false;
        }
    }

    public async Task<bool> AdjustAutopilotHeadingAsync(double deltaDeg)
    {
        try
        {
            var body = new { value = deltaDeg * Math.PI / 180.0 }; // Convert to radians
            var url = $"{_baseUrl}/signalk/v2/api/vessels/self/steering/autopilot/actions/adjustHeading";
            var response = await _http.PutAsJsonAsync(url, body);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to adjust autopilot heading by {Delta} deg", deltaDeg);
            return false;
        }
    }

    public string BaseUrl => _baseUrl;
}
