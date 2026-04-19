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
            || coords.ValueKind != JsonValueKind.Array
            || coords.GetArrayLength() == 0)
            return null;

        // The SignalK `/self/track` endpoint returns GeoJSON of type
        // LineString (flat array of points) when the track is continuous,
        // or MultiLineString (array of line segments) when there are
        // gaps. Detect which one we got by peeking at coordinates[0]:
        //   LineString:      coordinates[0] is [lon, lat, time?] (number-leaves)
        //   MultiLineString: coordinates[0] is an array of points
        // Before the fix the parser only handled MultiLineString, so a
        // continuous track silently decoded to zero points.
        var points = new List<double[]>();
        var first = coords[0];
        bool isMulti = first.ValueKind == JsonValueKind.Array
            && first.GetArrayLength() > 0
            && first[0].ValueKind == JsonValueKind.Array;

        if (isMulti)
        {
            foreach (var line in coords.EnumerateArray())
                AppendPoints(line, points);
        }
        else
        {
            AppendPoints(coords, points);
        }
        return points.Count == 0 ? null : [.. points];
    }

    // Each point is [lon, lat, (optional) time]. Leaflet needs [lat, lon].
    private static void AppendPoints(JsonElement line, List<double[]> dest)
    {
        foreach (var point in line.EnumerateArray())
        {
            if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() < 2)
                continue;
            var arr = new double[2];
            int i = 0;
            foreach (var val in point.EnumerateArray())
            {
                if (i < 2 && val.ValueKind == JsonValueKind.Number) arr[i++] = val.GetDouble();
            }
            if (i == 2) dest.Add([arr[1], arr[0]]);
        }
    }
}
