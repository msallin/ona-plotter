using System.Globalization;
using System.Text.Json;
using OnaPlotter.Models;

namespace OnaPlotter.Services.Api;

public sealed class TrackApi : ITrackApi
{
    private readonly HttpClient _http;
    private readonly ISignalKBaseUrl _baseUrl;
    private readonly HistoryCache? _cache;

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

    /// <summary>Production ctor: DI hands us the session cache.
    /// The two-arg overload below is a back-compat hatch for tests
    /// that don't care about caching -- they pass null and every
    /// chunk fetch goes through unconditionally.</summary>
    public TrackApi(HttpClient http, ISignalKBaseUrl baseUrl, HistoryCache cache)
    {
        _http = http;
        _baseUrl = baseUrl;
        _cache = cache;
    }

    /// <summary>Test-only overload: skip the cache. Lets pre-cache
    /// tests keep their original wiring without changing every
    /// instantiation site. New cache-behavior tests use the
    /// three-arg ctor with a fresh <see cref="HistoryCache"/>.</summary>
    public TrackApi(HttpClient http, ISignalKBaseUrl baseUrl)
        : this(http, baseUrl, cache: null!)
    {
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

    /// <summary>
    /// signalk-parquet (and signalk-to-influxdb2 by report) caps each
    /// History API response at 500 rows. The cap isn't documented or
    /// configurable -- the server silently re-buckets the requested
    /// window so the response fits, dropping the requested
    /// <c>resolution</c> when window/resolution &gt; 500. The fallout
    /// is most visible on the History page's segmenter: at 7d / 30s
    /// the helm asks for ~20 160 samples and gets 500 spaced ~20 min
    /// apart, so each state transition (stationary ↔ moving) lands
    /// on a sample boundary that's 20 min wide and the trips table
    /// shows a 20-min hole between consecutive segments.
    ///
    /// <para>We side-step the cap by chunking large windows
    /// client-side: each sub-request stays under the cap so the
    /// server returns the requested resolution unaltered. Constants
    /// are <c>internal</c> so unit tests can cover the chunking
    /// boundary without timing-dependent live-server calls.</para>
    /// </summary>
    internal const int MaxRowsPerRequest = 500;

    /// <summary>Safety margin under the cap. signalk-parquet
    /// occasionally returns 501 rows on edge windows (off-by-one in
    /// its own bucketing); 95% of the cap leaves headroom.</summary>
    internal const double ChunkSafetyFraction = 0.95;

    /// <inheritdoc/>
    public async Task<TrackPoint[]?> GetServerTrackPointsAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? timespan,
        string resolution = "30s",
        TrackBbox? bbox = null,
        CancellationToken ct = default)
    {
        string resExpr = string.IsNullOrWhiteSpace(resolution) ? "30s" : resolution;

        // Decide single-request vs paginated. The cap kicks in when
        // (window / resolution) > 500 (rows). For windows that fit
        // comfortably within the cap we keep the original single-
        // request shape (relative `duration=` when the caller didn't
        // pass an absolute range) so existing servers + tests + URL
        // shapes are unchanged. Only when the cap WOULD bite do we
        // expand into absolute-windowed chunks.
        TimeSpan? resSpan = TryParseResolutionToSpan(resExpr);
        TimeSpan? windowSpan = TryComputeWindowSpan(from, to, timespan);
        bool needsChunking = resSpan is TimeSpan rs && rs > TimeSpan.Zero
            && windowSpan is TimeSpan ws
            && ws.TotalSeconds / rs.TotalSeconds > MaxRowsPerRequest;

        if (!needsChunking)
        {
            // Single-request fast path: the URL shape (and its
            // relative `duration=` form when applicable) is what
            // every release prior to pagination shipped, so callers
            // and tests pinning the small-window contract carry
            // forward unchanged.
            return await FetchChunkAsync(from, to, timespan, resExpr, bbox, ct);
        }

        // Resolve absolute bounds for chunking. If the caller went
        // relative-only (timespan dropdown), pin "now" as the upper
        // bound; the chunks then walk backwards from there. The
        // first absolute bound we resolve is what every chunk
        // shares; computing once avoids per-chunk drift if the wall
        // clock advances mid-fetch.
        DateTimeOffset toAbs = to ?? DateTimeOffset.UtcNow;
        DateTimeOffset fromAbs = from ?? toAbs - windowSpan!.Value;

        // Chunk width: floor(cap × safety × resolution). The
        // safety fraction is below 1 so a server that off-by-ones
        // its own bucketing (occasional 501 returns observed) still
        // lands inside the cap.
        long chunkSeconds = (long)Math.Max(
            resSpan!.Value.TotalSeconds,
            Math.Floor(MaxRowsPerRequest * ChunkSafetyFraction * resSpan.Value.TotalSeconds));

        // Cache key prefix shared by every chunk in this pagination
        // loop. `paths` and `resolution` are stable across chunks;
        // only the `from`/`to` instants vary. Computing once avoids
        // re-joining the rich-paths array per iteration.
        string pathsKey = string.Join(',', RichPaths);

        var aggregated = new List<TrackPoint>();
        DateTimeOffset cursor = fromAbs;
        DateTime? lastBoundaryTimestamp = null;
        while (cursor < toAbs)
        {
            if (ct.IsCancellationRequested) break;
            DateTimeOffset chunkTo = cursor.AddSeconds(chunkSeconds);
            if (chunkTo > toAbs) chunkTo = toAbs;

            // Cache lookup. The cache itself enforces the live-tail
            // skip rule (chunks within HeadFreshness of "now" miss
            // unconditionally) so we don't have to repeat that test
            // here. Null cache = test-only ctor; behave as if every
            // lookup misses.
            var key = HistoryCache.Key.From(
                pathsKey, resExpr, cursor.UtcDateTime, chunkTo.UtcDateTime, bbox);
            var chunk = _cache?.TryGet(key);
            if (chunk is null)
            {
                chunk = await FetchChunkAsync(
                    from: cursor, to: chunkTo,
                    timespan: null, resolution: resExpr, bbox: bbox, ct: ct);
                // Store on success only. Null = transport / parse
                // failure; storing null would poison subsequent
                // loads of the same window. The cache's own
                // live-tail rule will silently no-op the head chunk
                // even when the response is non-null.
                if (chunk is { Length: > 0 }) _cache?.Set(key, chunk);
            }
            if (chunk is { Length: > 0 })
            {
                // Dedupe the seam: consecutive sub-requests can
                // return the same boundary timestamp because both
                // include the seam instant (SignalK's `from`/`to`
                // are documented inclusive on both ends). Skipping
                // a duplicated head sample keeps the segmenter from
                // seeing a zero-distance "hop" right at the seam
                // and emitting a spurious 1-point segment.
                int skip = 0;
                if (lastBoundaryTimestamp is DateTime t
                    && chunk[0].Timestamp == t) skip = 1;
                for (int i = skip; i < chunk.Length; i++) aggregated.Add(chunk[i]);
                lastBoundaryTimestamp = chunk[^1].Timestamp;
            }
            cursor = chunkTo;
        }
        return aggregated.Count == 0 ? null : [.. aggregated];
    }

    /// <summary>Single-shot fetch. The non-paginated path hits this
    /// once with the caller's range / timespan unchanged; the
    /// paginated path hits this N times with absolute windows it
    /// computed itself. Same URL-building and parsing rules either
    /// way -- the only thing the caller controls is the time
    /// expression in the query string.</summary>
    private async Task<TrackPoint[]?> FetchChunkAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? timespan,
        string resolution,
        TrackBbox? bbox,
        CancellationToken ct)
    {
        string pathsParam = string.Join(',', RichPaths);

        string url = _baseUrl.Combine(SignalKUrls.HistoryValuesPath)
            + $"?paths={Uri.EscapeDataString(pathsParam)}"
            + $"&resolution={Uri.EscapeDataString(resolution)}";

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

    /// <summary>Resolution shorthand → TimeSpan. Accepts the same
    /// dropdown values the History page emits ("30s", "1m", "5m",
    /// "15m") plus pass-through ISO 8601 duration strings ("PT30S")
    /// in case a future caller wires the canonical form. Returns
    /// null when nothing parseable comes through; the caller falls
    /// back to the single-request path so an unparseable resolution
    /// can never be the reason a fetch returns nothing.</summary>
    internal static TimeSpan? TryParseResolutionToSpan(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (s.StartsWith('P') || s.StartsWith('p'))
        {
            try { return System.Xml.XmlConvert.ToTimeSpan(s.ToUpperInvariant()); }
            catch (FormatException) { return null; }
        }
        if (s.Length < 2) return null;
        char unit = char.ToLowerInvariant(s[^1]);
        if (!int.TryParse(s.AsSpan(0, s.Length - 1), out int n) || n <= 0) return null;
        return unit switch
        {
            's' => TimeSpan.FromSeconds(n),
            'm' => TimeSpan.FromMinutes(n),
            'h' => TimeSpan.FromHours(n),
            'd' => TimeSpan.FromDays(n),
            _ => null,
        };
    }

    /// <summary>Window length used by the chunking decision. Picks
    /// the absolute (to - from) when the caller passed both, else
    /// the timespan dropdown value, else null. Pure helper so the
    /// chunking decision and the actual chunk sub-requests share
    /// one window-shape rule.</summary>
    internal static TimeSpan? TryComputeWindowSpan(
        DateTimeOffset? from, DateTimeOffset? to, string? timespan)
    {
        if (from is DateTimeOffset f)
        {
            var t = to ?? DateTimeOffset.UtcNow;
            var span = t - f;
            return span > TimeSpan.Zero ? span : null;
        }
        if (string.IsNullOrWhiteSpace(timespan)) return TimeSpan.FromDays(1);
        return TryParseResolutionToSpan(timespan);
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
