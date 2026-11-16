using System.Text.Json;
using OnaPlotter.Models;
using OnaPlotter.Utilities;

namespace OnaPlotter.Services.Api;

/// <summary>HTTP client for the signalk-charts-plugin REST surface
/// (<c>/signalk/v1/api/vessels/self/resources/charts</c> + the
/// plugin's per-chart metadata endpoint). Surfaces the helm-visible
/// chart catalogue (id, name, format, zoom range, tile URL) so the
/// Settings -&gt; Charts page can list / toggle / order them. Read-only;
/// chart installs happen via the SK admin UI, not here.</summary>
public sealed class ChartApi : IChartApi
{
    private readonly HttpClient _http;
    private readonly ISignalKBaseUrl _baseUrl;
    private readonly ILogger<ChartApi> _logger;

    public ChartApi(HttpClient http, ISignalKBaseUrl baseUrl, ILogger<ChartApi> logger)
    {
        _http = http;
        _baseUrl = baseUrl;
        _logger = logger;
    }

    /// <summary>
    /// Returns the helm-visible chart list: the built-in
    /// OSM + OpenSeaMap entries first (synthesised from
    /// <see cref="BuiltInCharts.All"/> with internet tile URLs), then
    /// every chart published by the SignalK server's charts API. The
    /// built-ins always appear regardless of SK-server reachability so
    /// the helm has a basemap available even on a failed first boot.
    /// An SK-server failure (transport, timeout, non-2xx, malformed
    /// body) is logged and yields just the built-ins; only a caller
    /// cancellation propagates.
    /// </summary>
    public async Task<List<SignalkChart>> GetAllAsync(CancellationToken ct = default)
    {
        // Built-ins go first so they show up at the top of the Layers
        // panel and the quick-bar, matching the visual convention of
        // "basemap stays under everything" (chart order is render
        // order, with the first-listed chart drawn lowest in the
        // z-stack - ChartLayerController.ApplyOrder controls this).
        var charts = new List<SignalkChart>(BuiltInCharts.All.Count + 8);
        charts.AddRange(BuiltInCharts.All);

        var url = _baseUrl.Combine(SignalKUrls.ChartsPath);
        var result = await ResourceHttp.GetJsonAsync<Dictionary<string, SignalkChart>>(
            _http, url, _logger, ct, JsonSerializerOptions.Web);
        if (!result.Success)
        {
            // SK server unreachable / non-2xx / malformed: the helm
            // still gets the built-in basemaps from the prefix above.
            // Failing the whole call would erase that fallback, which
            // is the point of having client-side basemaps at all.
            // Transport + parse failures are already logged by
            // ResourceHttp; a status failure is logged here.
            if (result.StatusCode is int status)
                _logger.LogWarning("Chart list GET returned HTTP {Status}; showing built-in charts only", status);
            return charts;
        }
        if (result.Value is null) return charts;

        foreach (var (key, chart) in result.Value)
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
