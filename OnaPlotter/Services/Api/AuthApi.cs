using System.Text.Json;
using OnaPlotter.Models;

namespace OnaPlotter.Services.Api;

/// <summary>signalk-server's <c>/skServer/loginStatus</c> probe.
/// The path is server-implementation-specific (not in the SK spec)
/// but every signalk-server build ships it; third-party servers may
/// 404 and we degrade gracefully to "unknown" (null).</summary>
public sealed class AuthApi : IAuthApi
{
    private readonly HttpClient _http;
    private readonly ISignalKBaseUrl _baseUrl;

    public AuthApi(HttpClient http, ISignalKBaseUrl baseUrl)
    {
        _http = http;
        _baseUrl = baseUrl;
    }

    public async Task<LoginStatus?> GetLoginStatusAsync(CancellationToken ct = default)
    {
        // Path is /skServer/loginStatus -- NOT under /signalk/. The
        // SK spec doesn't define an auth-status surface; signalk-
        // server's internal admin endpoint is the de-facto source.
        var url = _baseUrl.Combine("/skServer/loginStatus");
        HttpResponseMessage response;
        try { response = await _http.GetAsync(url, ct); }
        catch (HttpRequestException) { return null; }
        using (response)
        {
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                return JsonSerializer.Deserialize<LoginStatus>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException) { return null; }
        }
    }
}
