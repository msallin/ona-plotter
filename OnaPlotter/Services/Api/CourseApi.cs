using System.Net.Http.Json;

namespace OnaPlotter.Services.Api;

public sealed class CourseApi : ICourseApi
{
    private readonly HttpClient _http;
    private readonly ISignalKBaseUrl _baseUrl;

    public CourseApi(HttpClient http, ISignalKBaseUrl baseUrl)
    {
        _http = http;
        _baseUrl = baseUrl;
    }

    public async Task<bool> SetDestinationAsync(string waypointId, CancellationToken ct = default)
    {
        var body = new { href = $"/resources/waypoints/{Uri.EscapeDataString(waypointId)}" };
        var url = _baseUrl.Combine(SignalKUrls.CourseDestinationPath);
        using var response = await _http.PutAsJsonAsync(url, body, ct);
        return response.IsSuccessStatusCode;
    }

    /// <summary>Sets a direct lat/lon destination - used for the Stop
    /// Navigation undo path, where we want to restore to an arbitrary point
    /// without having to identify the original waypoint or route.</summary>
    public async Task<bool> SetDestinationPositionAsync(double latitude, double longitude, CancellationToken ct = default)
    {
        var body = new { position = new { latitude, longitude } };
        var url = _baseUrl.Combine(SignalKUrls.CourseDestinationPath);
        using var response = await _http.PutAsJsonAsync(url, body, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> SetActiveRouteAsync(string routeId, int pointIndex = 0,
        bool reverse = false, CancellationToken ct = default)
    {
        // SignalK v2 active-route body shape matches what Freeboard-SK
        // sends: href is the v1 relative resource path (NOT the v2 full
        // API path), pointIndex is zero-based, reverse flips the leg
        // direction. Servers that don't honour pointIndex will default
        // to the first leg, which is still the common case.
        var body = new
        {
            href = $"/resources/routes/{Uri.EscapeDataString(routeId)}",
            pointIndex,
            reverse
        };
        var url = _baseUrl.Combine(SignalKUrls.CourseActiveRoutePath);
        using var response = await _http.PutAsJsonAsync(url, body, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> AdvanceActiveRouteAsync(CancellationToken ct = default)
    {
        // Empty body PUT: SignalK's activeRoute/nextPoint endpoint
        // increments the server-side pointIndex by 1. Returns 404 when
        // no active route exists -- caller reads IsSuccessStatusCode.
        var url = _baseUrl.Combine(SignalKUrls.CourseActiveRouteNextPointPath);
        using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        using var response = await _http.PutAsync(url, content, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> ClearAsync(CancellationToken ct = default)
    {
        var url = _baseUrl.Combine(SignalKUrls.CoursePath);
        using var response = await _http.DeleteAsync(url, ct);
        return response.IsSuccessStatusCode;
    }
}
