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

    public async Task<double[][]?> GetServerTrackAsync(string timespan = "1d", string resolution = "1m", CancellationToken ct = default)
    {
        var url = _baseUrl.Combine(SignalKUrls.Track(timespan, resolution));
        using var response = await _http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("coordinates", out var coords)
            || coords.ValueKind != JsonValueKind.Array) return null;

        var points = new List<double[]>();
        foreach (var line in coords.EnumerateArray())
        {
            foreach (var point in line.EnumerateArray())
            {
                int i = 0;
                var arr = new double[2];
                foreach (var val in point.EnumerateArray())
                {
                    if (i < 2) arr[i++] = val.GetDouble();
                }
                // GeoJSON [lon, lat] -> Leaflet [lat, lon].
                points.Add([arr[1], arr[0]]);
            }
        }
        return [.. points];
    }
}
