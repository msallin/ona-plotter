using System.Globalization;
using System.Text.Json;
using OnaPlotter.Models;

namespace OnaPlotter.Services.Api;

public sealed class TrackApi : ITrackApi
{
    private readonly HttpClient _http;
    private readonly ISignalKBaseUrl _baseUrl;

    /// <summary>SignalK paths the rich fetch asks for. Order matters
    /// because the response's <c>data</c> rows align element-per-path,
    /// and the parser indexes into them positionally. Position MUST
    /// be index 0; if it's missing the row is skipped entirely
    /// (no point with no fix).</summary>
    private static readonly string[] RichPaths =
    [
        OnaPlotter.Utilities.SkPaths.Navigation.Position,
        OnaPlotter.Utilities.SkPaths.Navigation.SpeedOverGround,
        OnaPlotter.Utilities.SkPaths.Navigation.CourseOverGroundTrue,
        OnaPlotter.Utilities.SkPaths.Navigation.HeadingTrue,
        OnaPlotter.Utilities.SkPaths.Environment.Wind.SpeedTrue,
        OnaPlotter.Utilities.SkPaths.Environment.Wind.AngleTrueWater,
    ];

    public TrackApi(HttpClient http, ISignalKBaseUrl baseUrl)
    {
        _http = http;
        _baseUrl = baseUrl;
    }

    /// <summary>
    /// Fetches recorded position history from the SignalK History API v2
    /// (<c>/signalk/v2/api/history/values</c>). The
    /// resolution-tunable surface is implemented by signalk-parquet and
    /// signalk-to-influxdb2; OnaPlotter requires one of these to be
    /// installed because the resolution the History page asks for cannot
    /// be honoured by the fixed-cadence track resource APIs.
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
            + $"?paths={Uri.EscapeDataString(OnaPlotter.Utilities.SkPaths.Navigation.Position)}"
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

                    if (TryParsePosition(value, out double lat, out double lon))
                        points.Add([lat, lon]);
                }
                return points.Count == 0 ? null : [.. points];
            }
        }
    }

    /// <inheritdoc/>
    public async Task<TrackPoint[]?> GetServerTrackPointsAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? timespan,
        string resolution = "30s",
        TrackBbox? bbox = null,
        CancellationToken ct = default)
    {
        // Build the time window. Either an absolute (from + to) or
        // relative (duration) query goes to the API; the History page
        // uses absolute when the helm has picked a date range, relative
        // when the dropdown is in "last X" mode.
        string resExpr = string.IsNullOrWhiteSpace(resolution) ? "30s" : resolution;
        string pathsParam = string.Join(',', RichPaths);

        string url = _baseUrl.Combine(SignalKUrls.HistoryValuesPath)
            + $"?paths={Uri.EscapeDataString(pathsParam)}"
            + $"&resolution={Uri.EscapeDataString(resExpr)}";

        if (from is DateTimeOffset fromUtc)
        {
            // Absolute window. Default the upper bound to "now" if the
            // caller passed only `from` -- common for "everything since
            // yesterday at 8am".
            var toUtc = to ?? DateTimeOffset.UtcNow;
            url += $"&from={Uri.EscapeDataString(fromUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture))}"
                + $"&to={Uri.EscapeDataString(toUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture))}";
        }
        else
        {
            // Relative window. Falls back to 24 hours when no timespan
            // was given -- a sensible default for the History page's
            // first paint.
            string isoDuration = ToIsoDuration(timespan ?? "1d");
            url += $"&duration={Uri.EscapeDataString(isoDuration)}";
        }

        if (bbox is TrackBbox b)
        {
            // bbox query convention: south,west,north,east. Same order
            // Leaflet's Bounds.toBBoxString uses, same order GeoJSON's
            // bbox member uses (modulo lon/lat swap -- GeoJSON is
            // (west,south,east,north); we go (south,west,north,east)
            // here because that's the order signalk-parquet's bbox
            // extension accepts and our server is the source of truth).
            // Servers that don't recognise bbox just ignore it; the
            // page falls back to client-side filtering.
            url += "&bbox="
                + Uri.EscapeDataString(string.Format(CultureInfo.InvariantCulture,
                    "{0},{1},{2},{3}", b.South, b.West, b.North, b.East));
        }

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
                return ParseRichResponse(doc);
            }
        }
    }

    /// <summary>Parses the multi-path response. Walks the
    /// <c>values</c> array to learn which response column carries
    /// which path (the server may reorder, drop unsupported paths,
    /// etc.), then iterates <c>data</c> rows positionally. Public-
    /// internal so the unit tests can drive it directly without
    /// standing up a fake HTTP server.</summary>
    internal static TrackPoint[]? ParseRichResponse(JsonDocument doc)
    {
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty("values", out var values)
            || values.ValueKind != JsonValueKind.Array) return null;
        if (!root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array
            || data.GetArrayLength() == 0) return null;

        // Resolve the column index for each path we care about. Server
        // can reorder; can omit paths the provider doesn't have. -1
        // means "this path was not in the response", and the parser
        // falls back to null for the corresponding TrackPoint field.
        int posIdx = -1, sogIdx = -1, cogIdx = -1, hdgIdx = -1, twsIdx = -1, twaIdx = -1;
        int colNum = 0;
        foreach (var v in values.EnumerateArray())
        {
            if (v.ValueKind == JsonValueKind.Object
                && v.TryGetProperty("path", out var pathEl)
                && pathEl.ValueKind == JsonValueKind.String)
            {
                var path = pathEl.GetString();
                switch (path)
                {
                    case OnaPlotter.Utilities.SkPaths.Navigation.Position: posIdx = colNum; break;
                    case OnaPlotter.Utilities.SkPaths.Navigation.SpeedOverGround: sogIdx = colNum; break;
                    case OnaPlotter.Utilities.SkPaths.Navigation.CourseOverGroundTrue: cogIdx = colNum; break;
                    case OnaPlotter.Utilities.SkPaths.Navigation.HeadingTrue: hdgIdx = colNum; break;
                    case OnaPlotter.Utilities.SkPaths.Environment.Wind.SpeedTrue: twsIdx = colNum; break;
                    case OnaPlotter.Utilities.SkPaths.Environment.Wind.AngleTrueWater: twaIdx = colNum; break;
                }
            }
            colNum++;
        }
        // Position is mandatory: a track without a fix is just a
        // sequence of speed values with no point to put them on.
        if (posIdx < 0) return null;

        var points = new List<TrackPoint>(data.GetArrayLength());
        foreach (var entry in data.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Array) continue;
            int len = entry.GetArrayLength();
            // Every row must have at least one column past the
            // timestamp; without position there's nothing to plot.
            if (len <= posIdx + 1) continue;

            // Timestamp column 0 -- ISO 8601 string per SK History API.
            if (entry[0].ValueKind != JsonValueKind.String) continue;
            if (!DateTime.TryParse(entry[0].GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var ts)) continue;

            // +1 because column 0 is the timestamp; values start at index 1.
            var posCol = entry[posIdx + 1];
            if (posCol.ValueKind == JsonValueKind.Null) continue;
            if (!TryParsePosition(posCol, out double lat, out double lon)) continue;

            points.Add(new TrackPoint(
                Timestamp: ts,
                Latitude: lat,
                Longitude: lon,
                SpeedOverGround: TryGetNumber(entry, sogIdx),
                CourseOverGround: TryGetNumber(entry, cogIdx),
                Heading: TryGetNumber(entry, hdgIdx),
                WindAngleApparent: null,        // not fetched -- the AWS/AWA sensors give no useful history at 30 s grain
                WindSpeedApparent: null,
                WindAngleTrue: TryGetNumber(entry, twaIdx),
                WindSpeedTrue: TryGetNumber(entry, twsIdx)));
        }
        return points.Count == 0 ? null : [.. points];
    }

    /// <summary>Parse a <c>navigation.position</c> value cell. Both
    /// the array form (<c>[lon, lat]</c>, signalk-parquet default) and
    /// the object form (<c>{longitude, latitude}</c>, some influx
    /// setups) are tolerated -- the History page shouldn't have to know
    /// which provider is configured.</summary>
    private static bool TryParsePosition(JsonElement value, out double lat, out double lon)
    {
        lat = 0; lon = 0;
        if (value.ValueKind == JsonValueKind.Array
            && value.GetArrayLength() >= 2
            && value[0].ValueKind == JsonValueKind.Number
            && value[1].ValueKind == JsonValueKind.Number)
        {
            lon = value[0].GetDouble();
            lat = value[1].GetDouble();
            return true;
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            bool ok = true;
            if (value.TryGetProperty("latitude", out var la)
                && la.ValueKind == JsonValueKind.Number)
                lat = la.GetDouble();
            else ok = false;
            if (value.TryGetProperty("longitude", out var lo)
                && lo.ValueKind == JsonValueKind.Number)
                lon = lo.GetDouble();
            else ok = false;
            return ok;
        }
        return false;
    }

    private static double? TryGetNumber(JsonElement row, int colIdx)
    {
        if (colIdx < 0) return null;
        // +1 because column 0 is the timestamp.
        int dataIdx = colIdx + 1;
        if (dataIdx >= row.GetArrayLength()) return null;
        var cell = row[dataIdx];
        return cell.ValueKind == JsonValueKind.Number ? cell.GetDouble() : null;
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
