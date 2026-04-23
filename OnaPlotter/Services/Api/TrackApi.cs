using System.Text.Json;

namespace OnaPlotter.Services.Api;

public sealed class TrackApi : ITrackApi
{
    private readonly HttpClient _http;
    private readonly ISignalKBaseUrl _baseUrl;

    public TrackApi(HttpClient http, ISignalKBaseUrl baseUrl)
    {
        _http = http;
        _baseUrl = baseUrl;
    }

    /// <summary>
    /// Fetches recorded position history from the SignalK History API v2
    /// (<c>/signalk/v2/api/history/values</c>). That's the modern,
    /// resolution-tunable surface implemented by signalk-parquet and
    /// signalk-to-influxdb2.
    ///
    /// Earlier versions fell back to <c>/signalk/v1/api/self/track</c>
    /// and <c>/signalk/v2/api/resources/tracks</c>, but we dropped both
    /// on user request: the deployment targets OnaPlotter runs against
    /// all have a history provider, and the two track fallbacks
    /// returned at the recorder's fixed cadence instead of honouring
    /// the resolution the History page asked for.
    ///
    /// Returns null when the history surface returns no data (empty
    /// window, provider missing, plugin not installed) so the History
    /// page can show its "No track data for this timespan" hint.
    /// </summary>
    public async Task<double[][]?> GetServerTrackAsync(
        string timespan = "1d", string resolution = "1m",
        CancellationToken ct = default)
    {
        // The History API wants ISO 8601 durations (PT1H, P1D) and a
        // resolution expression that accepts the shorthand we already
        // use elsewhere (30s, 1m). Translate the History-page dropdown
        // shorthand before building the query.
        string isoDuration = ToIsoDuration(timespan);
        string resExpr = string.IsNullOrWhiteSpace(resolution) ? "30s" : resolution;

        var url = _baseUrl.Combine(SignalKUrls.HistoryValuesPath)
            + $"?paths={Uri.EscapeDataString("navigation.position")}"
            + $"&duration={Uri.EscapeDataString(isoDuration)}"
            + $"&resolution={Uri.EscapeDataString(resExpr)}";

        HttpResponseMessage response;
        try { response = await _http.GetAsync(url, ct); }
        catch (HttpRequestException) { return null; }
        using (response)
        {
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(json)) return null;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(json); }
            catch (JsonException) { return null; }
            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
                if (!doc.RootElement.TryGetProperty("data", out var data)
                    || data.ValueKind != JsonValueKind.Array
                    || data.GetArrayLength() == 0) return null;

                // Each entry is [timestamp, value]. For navigation.position
                // the value shape differs by provider:
                //   - [lon, lat]            (array; signalk-parquet default)
                //   - {longitude, latitude} (object; some influx setups)
                // Handle both because the History page shouldn't care
                // which history provider the server has configured.
                var points = new List<double[]>(data.GetArrayLength());
                foreach (var entry in data.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Array
                        || entry.GetArrayLength() < 2) continue;
                    var value = entry[1];
                    if (value.ValueKind == JsonValueKind.Null) continue;

                    double? lat = null, lon = null;
                    if (value.ValueKind == JsonValueKind.Array
                        && value.GetArrayLength() >= 2
                        && value[0].ValueKind == JsonValueKind.Number
                        && value[1].ValueKind == JsonValueKind.Number)
                    {
                        lon = value[0].GetDouble();
                        lat = value[1].GetDouble();
                    }
                    else if (value.ValueKind == JsonValueKind.Object)
                    {
                        if (value.TryGetProperty("latitude", out var la)
                            && la.ValueKind == JsonValueKind.Number)
                            lat = la.GetDouble();
                        if (value.TryGetProperty("longitude", out var lo)
                            && lo.ValueKind == JsonValueKind.Number)
                            lon = lo.GetDouble();
                    }

                    if (lat is double latV && lon is double lonV)
                        points.Add([latV, lonV]);
                }
                return points.Count == 0 ? null : [.. points];
            }
        }
    }

    // Convert History page dropdown values (1h, 6h, 1d, 3d, 7d) into
    // ISO 8601 duration strings (PT1H, PT6H, P1D, P3D, P7D). Unknown
    // shapes pass through unchanged so a caller that already has an
    // ISO duration (PT15M, P2D) stays valid.
    internal static string ToIsoDuration(string timespan)
    {
        if (string.IsNullOrWhiteSpace(timespan)) return "P1D";
        if (timespan.StartsWith('P') || timespan.StartsWith('p')) return timespan;
        if (timespan.Length < 2) return "P1D";
        char unit = char.ToLowerInvariant(timespan[^1]);
        if (!int.TryParse(timespan.AsSpan(0, timespan.Length - 1), out int n) || n <= 0)
            return "P1D";
        return unit switch
        {
            's' => $"PT{n}S",
            'm' => $"PT{n}M",
            'h' => $"PT{n}H",
            'd' => $"P{n}D",
            _ => "P1D",
        };
    }
}
