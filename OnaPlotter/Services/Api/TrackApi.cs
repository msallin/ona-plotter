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
    /// Two SignalK surfaces expose recorded tracks and they don't
    /// overlap on every install:
    ///
    ///   v1: <c>/signalk/v1/api/self/track?timespan=...&amp;resolution=...</c>
    ///       exposed by signalk-tracks / signalk-parquet. Returns a
    ///       single GeoJSON LineString or MultiLineString covering the
    ///       whole window.
    ///   v2: <c>/signalk/v2/api/resources/tracks</c> from signalk-server's
    ///       built-in track-recording (or @signalk/tracks). Returns a
    ///       dict of {track-id: {feature, timestamp, ...}} with one
    ///       entry per recorded segment. No server-side timespan
    ///       parameter, so the client filters by each track's
    ///       <c>timestamp</c> and concatenates matching segments.
    ///
    /// Try v1 first (cheaper round-trip when it's available), then
    /// fall through to v2. Both parsers flip [lon, lat] -> [lat, lon]
    /// for Leaflet consumption and drop any track point that doesn't
    /// parse cleanly. Returns null when NEITHER surface has data in
    /// the window, so the History page can show the "No track data"
    /// hint.
    /// </summary>
    public async Task<double[][]?> GetServerTrackAsync(string timespan = "1d", string resolution = "1m", CancellationToken ct = default)
    {
        var v1 = await TryGetV1TrackAsync(timespan, resolution, ct);
        if (v1 is { Length: > 0 }) return v1;

        return await TryGetV2TracksAsync(timespan, ct);
    }

    private async Task<double[][]?> TryGetV1TrackAsync(string timespan, string resolution, CancellationToken ct)
    {
        var url = _baseUrl.Combine(SignalKUrls.Track(timespan, resolution));
        HttpResponseMessage response;
        try { response = await _http.GetAsync(url, ct); }
        catch (HttpRequestException) { return null; }
        using (response)
        {
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("coordinates", out var coords)
                || coords.ValueKind != JsonValueKind.Array
                || coords.GetArrayLength() == 0)
                return null;

            // LineString: coordinates[0] is [lon, lat, time?].
            // MultiLineString: coordinates[0] is an array of points.
            // The parser used to only handle MultiLineString so a
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
    }

    private async Task<double[][]?> TryGetV2TracksAsync(string timespan, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow - ParseTimespan(timespan);

        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(
                _baseUrl.Combine(SignalKUrls.TracksPath), ct);
        }
        catch (HttpRequestException) { return null; }
        using (response)
        {
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

            // Collect (timestamp, coords[]) tuples for tracks whose
            // root timestamp falls inside the window, then emit them
            // ordered oldest-first so the playback slider advances
            // chronologically across a multi-segment day.
            var matching = new List<(DateTimeOffset ts, JsonElement coords)>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var t = prop.Value;
                if (t.ValueKind != JsonValueKind.Object) continue;
                if (!t.TryGetProperty("timestamp", out var tsEl)
                    || tsEl.ValueKind != JsonValueKind.String) continue;
                if (!DateTimeOffset.TryParse(tsEl.GetString(),
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal,
                        out var ts)) continue;
                if (ts < cutoff) continue;

                if (!t.TryGetProperty("feature", out var feat)
                    || !feat.TryGetProperty("geometry", out var geom)
                    || !geom.TryGetProperty("coordinates", out var coords)
                    || coords.ValueKind != JsonValueKind.Array) continue;

                matching.Add((ts, coords));
            }

            matching.Sort((a, b) => a.ts.CompareTo(b.ts));

            var points = new List<double[]>();
            foreach (var (_, coords) in matching)
            {
                if (coords.GetArrayLength() == 0) continue;
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
            }
            return points.Count == 0 ? null : [.. points];
        }
    }

    // Accepts the History page's "1h" / "6h" / "1d" / "3d" / "7d"
    // shorthand. Anything else falls back to 1 day so an unknown
    // value doesn't return zero-window (no matches) silently.
    private static TimeSpan ParseTimespan(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length < 2) return TimeSpan.FromDays(1);
        char unit = s[^1];
        if (!int.TryParse(s.AsSpan(0, s.Length - 1), out int n) || n <= 0)
            return TimeSpan.FromDays(1);
        return unit switch
        {
            'h' or 'H' => TimeSpan.FromHours(n),
            'd' or 'D' => TimeSpan.FromDays(n),
            'm' or 'M' => TimeSpan.FromMinutes(n),
            _ => TimeSpan.FromDays(1),
        };
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
