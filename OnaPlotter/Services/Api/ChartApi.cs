using System.Net.Http.Json;
using OnaPlotter.Models;

namespace OnaPlotter.Services.Api;

public sealed class ChartApi : IChartApi
{
    private readonly HttpClient _http;
    private readonly ISignalKBaseUrl _baseUrl;

    public ChartApi(HttpClient http, ISignalKBaseUrl baseUrl)
    {
        _http = http;
        _baseUrl = baseUrl;
    }

    public async Task<List<SignalkChart>> GetAllAsync(CancellationToken ct = default)
    {
        var url = _baseUrl.Combine(SignalKUrls.ChartsPath);
        using var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var dict = await response.Content.ReadFromJsonAsync<Dictionary<string, SignalkChart>>(cancellationToken: ct);
        if (dict is null) return [];

        var charts = new List<SignalkChart>(dict.Count);
        foreach (var (key, chart) in dict)
        {
            if (string.IsNullOrEmpty(chart.Identifier)) chart.Identifier = key;

            // Resolve relative tile URLs against the SignalK server origin.
            var tileUrl = chart.GetTileUrl();
            if (tileUrl is not null && tileUrl.StartsWith('/'))
                chart.TilemapUrl = _baseUrl.BaseUrl + tileUrl;

            if (chart.GetTileUrl() is not null) charts.Add(chart);
        }
        return charts;
    }
}
