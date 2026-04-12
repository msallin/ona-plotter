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

    public string BaseUrl => _baseUrl;
}
