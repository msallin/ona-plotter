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

    public async Task<bool> ClearAsync(CancellationToken ct = default)
    {
        var url = _baseUrl.Combine(SignalKUrls.CoursePath);
        using var response = await _http.DeleteAsync(url, ct);
        return response.IsSuccessStatusCode;
    }
}
