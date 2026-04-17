using System.Net.Http.Json;

namespace OnaPlotter.Services.Api;

public sealed class AutopilotApi : IAutopilotApi
{
    private readonly HttpClient _http;
    private readonly ISignalKBaseUrl _baseUrl;

    public AutopilotApi(HttpClient http, ISignalKBaseUrl baseUrl)
    {
        _http = http;
        _baseUrl = baseUrl;
    }

    public async Task<bool> SetStateAsync(string state, CancellationToken ct = default)
    {
        var url = _baseUrl.Combine(SignalKUrls.AutopilotStatePath);
        using var response = await _http.PutAsJsonAsync(url, new { value = state }, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> AdjustHeadingAsync(double deltaDeg, CancellationToken ct = default)
    {
        var url = _baseUrl.Combine(SignalKUrls.AutopilotAdjustHeadingPath);
        var body = new { value = deltaDeg * Math.PI / 180.0 };
        using var response = await _http.PutAsJsonAsync(url, body, ct);
        return response.IsSuccessStatusCode;
    }
}
