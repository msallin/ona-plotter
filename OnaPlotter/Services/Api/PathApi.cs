using System.Text.Json;
using OnaPlotter.Utilities;

namespace OnaPlotter.Services.Api;

/// <summary>Thin HTTP client for arbitrary SignalK v1
/// <c>/signalk/v1/api/...</c> path reads. Used by one-off seed +
/// recovery flows (e.g. Map.razor's anchor-tide bootstrap, the WS
/// reconnect re-sync) where the live delta stream hasn't yet
/// delivered a value the page needs to render. Returns the raw value
/// JSON node so callers parse the path-specific shape themselves.</summary>
public sealed class PathApi : IPathApi
{
    private readonly HttpClient _http;
    private readonly ISignalKBaseUrl _baseUrl;

    public PathApi(HttpClient http, ISignalKBaseUrl baseUrl)
    {
        _http = http;
        _baseUrl = baseUrl;
    }

    public async Task<List<string>> GetAvailablePathsAsync(CancellationToken ct = default)
    {
        var url = _baseUrl.Combine(SignalKUrls.SelfVesselPath);
        using var response = await _http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return [];

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.FlattenSignalKPaths();
    }
}
