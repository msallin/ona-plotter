using System.Net.Http.Json;
using OnaPlotter.Models;
using OnaPlotter.Utilities;

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

    /// <summary>
    /// Returns the helm-visible chart list: the built-in
    /// OSM + OpenSeaMap entries first (synthesised from
    /// <see cref="BuiltInCharts.All"/> with internet tile URLs), then
    /// every chart published by the SignalK server's charts API. The
    /// built-ins always appear regardless of SK-server reachability so
    /// the helm has a basemap available even on a failed first boot.
    /// SK-server failure surfaces as JSON / HTTP exceptions to the
    /// caller (Map.razor's SafeLoad wrapper toasts them).
    /// </summary>
    public async Task<List<SignalkChart>> GetAllAsync(CancellationToken ct = default)
    {
        // Built-ins go first so they show up at the top of the Layers
        // panel and the quick-bar, matching the visual convention of
        // "basemap stays under everything" (chart order is render
        // order, with the first-listed chart drawn lowest in the
        // z-stack -- ChartLayerController.ApplyOrder controls this).
        var charts = new List<SignalkChart>(BuiltInCharts.All.Count + 8);
        charts.AddRange(BuiltInCharts.All);

        try
        {
            var url = _baseUrl.Combine(SignalKUrls.ChartsPath);
            using var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var dict = await response.Content.ReadFromJsonAsync<Dictionary<string, SignalkChart>>(cancellationToken: ct);
            if (dict is null) return charts;

            foreach (var (key, chart) in dict)
            {
                if (string.IsNullOrEmpty(chart.Identifier)) chart.Identifier = key;

                // Resolve relative tile URLs against the SignalK server origin.
                var tileUrl = chart.GetTileUrl();
                if (tileUrl is not null && tileUrl.StartsWith('/'))
                    chart.TilemapUrl = _baseUrl.BaseUrl + tileUrl;

                if (chart.GetTileUrl() is not null) charts.Add(chart);
            }
        }
        catch (HttpRequestException ex)
        {
            // SK server unreachable / 5xx: the helm still gets the
            // built-in basemap charts from the prefix above. Re-throw
            // would erase that fallback (Map.razor's SafeLoad would
            // null the list), which defeats the purpose of having
            // client-side basemaps at all. Console.WriteLine so a
            // developer inspecting DevTools sees the SK failure
            // without the errorRelayBoot.js console.error wrapper
            // surfacing it as an unhandled error -- this is fully
            // recovered by the basemap fallback.
            Console.WriteLine($"[charts] SK fetch failed: {ex.Message}");
        }
        return charts;
    }
}
