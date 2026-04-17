using System.Text.Json;
using OnaPlotter.Utilities;

namespace OnaPlotter.Services.Api;

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
