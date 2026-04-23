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
    /// Three SignalK surfaces expose recorded position history and
    /// none of them is present on every install:
    ///
    ///   History API v2: <c>/signalk/v2/api/history/values</c>
    ///     (signalk-parquet, signalk-to-influxdb2). Path-value query
    ///     with tunable resolution + aggregation. Returns a clean
    ///     <c>[timestamp, [lon,lat]]</c> stream. This is the modern
    ///     standard and we try it first.
    ///
    ///   v2 tracks: <c>/signalk/v2/api/resources/tracks</c> from
    ///     signalk-server's built-in track recorder (or @signalk/
    ///     tracks). Returns a dict of {track-id: {feature, timestamp}}
    ///     with one entry per recorded segment. No server-side
    ///     timespan filter; we filter each track by its root
    ///     <c>timestamp</c> and concatenate matching segments.
    ///
    ///   v1 track: <c>/signalk/v1/api/self/track?timespan=...&amp;resolution=...</c>
    ///     from signalk-tracks / older signalk-parquet builds. Returns
    ///     a single GeoJSON LineString / MultiLineString covering the
    ///     whole window.
    ///
    /// All three parsers flip [lon, lat] -> [lat, lon] for Leaflet and
    /// drop any point that doesn't parse. Returns null only when ALL
    /// surfaces return nothing, so the History page can show the
    /// "No track data for this timespan" hint.
    /// </summary>
    public async Task<double[][]?> GetServerTrackAsync(string timespan = "1d", string resolution = "1m", CancellationToken ct = default)
    {
        var history = await TryGetHistoryAsync(timespan, resolution, ct);
        if (history is { Length: > 0 }) return history;

        var v1 = await TryGetV1TrackAsync(timespan, resolution, ct);
        if (v1 is { Length: > 0 }) return v1;

        return await TryGetV2TracksAsync(timespan, ct);
    }

    /// <summary>
    /// SignalK History API v2: query position history directly.
    /// Preferred source on modern installs -- we get resolution
    /// control + aggregation for free, and the response is one
    /// [timestamp, value] stream rather than a nested GeoJSON
    /// structure.
    /// </summary>
    private async Task<double[][]?> TryGetHistoryAsync(string timespan, string resolution, CancellationToken ct)
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
            catch (JsonException) { return null; } // non-JSON body -> fall back to next source
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
            // Handle both because the History page shouldn't care which
            // history plugin the server uses.
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
