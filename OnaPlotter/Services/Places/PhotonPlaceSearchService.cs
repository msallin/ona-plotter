using System.Net.Http;
using System.Text.Json;
using OnaPlotter.Services.Json;

namespace OnaPlotter.Services.Places;

/// <summary>
/// Photon (komoot) geocoder client. Free, no key, no User-Agent
/// requirement, fuzzy match across the global OSM corpus. Documented
/// at <c>https://photon.komoot.io/</c>; production endpoint
/// <c>https://photon.komoot.io/api/?q=...&amp;limit=N</c>.
///
/// <para>Provider behaviour relevant to this client:</para>
/// <list type="bullet">
///   <item><description>Fuzzy match -- "fowl cay" returns the right
///     "Fowl Cay" without the helm having to spell it precisely.</description></item>
///   <item><description>Returns a GeoJSON FeatureCollection;
///     coordinates are <c>[lon, lat]</c> per RFC 7946.</description></item>
///   <item><description>No rate limit on the public service that's
///     practically a problem for a single-helm boat (their FAQ
///     warns against bulk pipelines, not interactive use).</description></item>
///   <item><description>HTTPS-only.</description></item>
/// </list>
///
/// <para>Failure handling matches the
/// <see cref="IPlaceSearchService"/> contract: every transient
/// error path returns an empty list. The cache decorator stores
/// hit-results only, so an offline-window of empty responses
/// doesn't poison the cache; the next online call refills.</para>
/// </summary>
public sealed class PhotonPlaceSearchService : IPlaceSearchService
{
    /// <summary>Result limit on the Photon URL. 10 fits the
    /// dropdown without scrolling on a tablet; reaching for more
    /// would drown the helm in irrelevant matches anyway.</summary>
    public const int ResultLimit = 10;

    /// <summary>Per-call HTTP cap. Tighter than the shared 8 s
    /// ambient HttpClient timeout: a search that takes longer than
    /// 4 s is dead from the helm's POV (they've probably typed
    /// another character by then). Cancelling early frees the
    /// inner cancellation token before the dropdown stays empty.</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(4);

    private const string EndpointBase = "https://photon.komoot.io/api/";

    private readonly HttpClient _http;
    private readonly ILogger<PhotonPlaceSearchService> _logger;
    /// <summary>Returns the helm's current position (or null if not
    /// yet known) so each Photon call can bias results by proximity
    /// via &amp;lat=...&amp;lon=... params. The helm searching "marina"
    /// from the Mediterranean wants Mediterranean marinas first, not
    /// the global ranking. DI threads this in from
    /// <c>SignalkClient.Data</c>; tests can pass null or a stub.</summary>
    private readonly Func<(double Lat, double Lon)?>? _selfPosition;

    public PhotonPlaceSearchService(
        HttpClient http,
        ILogger<PhotonPlaceSearchService> logger,
        Func<(double Lat, double Lon)?>? selfPosition = null)
    {
        _http = http;
        _logger = logger;
        _selfPosition = selfPosition;
    }

    public async Task<IReadOnlyList<PlaceResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        // Linked CTS so an external cancel (helm typed another
        // char) wins immediately, but the call also self-times-
        // out at CallTimeout if Photon is slow.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(CallTimeout);

        // Query parameters per photon.komoot.io:
        //   q=<text>          : the helm-typed search string
        //   limit=N           : cap on results (we render at most 10 in the dropdown)
        //   dedupe            : Photon-side de-duplication so multiple OSM rows for
        //                       the same location collapse to one row (e.g. node +
        //                       way + relation for a city all reduce to one hit).
        //   osm_tag=place     : restrict to OSM elements tagged place=* (city,
        //                       town, village, island, harbour, ...) -- the helm
        //                       is looking for navigable destinations, not
        //                       roads / buildings / POIs that the default
        //                       Photon ranking otherwise mixes in.
        //   lat,lon           : OPTIONAL bias by proximity to the helm's current
        //                       fix. Re-ranks results so a "marina" search from
        //                       the Mediterranean returns Mediterranean marinas
        //                       before global ones. Skipped before the first
        //                       SignalK position fix lands (selfPosition() is null).
        var url = EndpointBase
            + "?q=" + Uri.EscapeDataString(query)
            + "&limit=" + ResultLimit
            + "&dedupe"
            + "&osm_tag=place";

        if (_selfPosition?.Invoke() is (double lat, double lon)
            && double.IsFinite(lat) && double.IsFinite(lon))
        {
            url += "&lat=" + lat.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)
                +  "&lon=" + lon.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(url, timeoutCts.Token);
        }
        catch (TaskCanceledException)
        {
            // Either the helm typed another char (external ct)
            // OR Photon was slow (timeoutCts). Either way: empty
            // list, the next keystroke will retry.
            _logger.LogInformation("[place] photon timed out / cancelled for query '{Query}'", query);
            return [];
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("[place] photon http failed for query '{Query}': {Message}",
                query, ex.Message);
            return [];
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[place] photon returned HTTP {StatusCode} for query '{Query}'",
                    (int)response.StatusCode, query);
                return [];
            }

            PhotonResponse? parsed;
            try
            {
                var json = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                if (string.IsNullOrWhiteSpace(json)) return [];
                parsed = JsonSerializer.Deserialize(json, OnaJsonContext.Default.PhotonResponse);
            }
            // ReadAsStringAsync over a flaky LTE link can drop the
            // connection mid-body and surface as HttpRequestException
            // (wrapping IOException) -- not JsonException. Without
            // this widened catch the exception escapes past the
            // empty-list-on-failure contract into SearchBox.OnInput.
            catch (Exception ex) when (ex is JsonException
                                          or HttpRequestException
                                          or IOException)
            {
                _logger.LogWarning("[place] photon read/parse failed for query '{Query}': {Message}",
                    query, ex.Message);
                return [];
            }
            catch (TaskCanceledException)
            {
                // Mid-body cancellation (helm typed another char OR
                // CallTimeout fired). Same shape as the pre-send
                // cancellation: empty list, log nothing extra.
                return [];
            }

            if (parsed?.Features is null || parsed.Features.Length == 0) return [];

            var results = new List<PlaceResult>(parsed.Features.Length);
            foreach (var feature in parsed.Features)
            {
                if (TryMapFeature(feature) is { } mapped) results.Add(mapped);
            }
            return results;
        }
    }

    /// <summary>
    /// Convert one Photon feature to a dropdown row. Returns null
    /// when the feature lacks the minimum coords + name we need to
    /// render. Skipping a single bad row is preferable to dropping
    /// the whole result set on a malformed response.
    /// </summary>
    internal static PlaceResult? TryMapFeature(PhotonFeature? feature)
    {
        if (feature?.Geometry?.Coordinates is not { Length: >= 2 } coords) return null;
        if (!double.IsFinite(coords[0]) || !double.IsFinite(coords[1])) return null;

        var props = feature.Properties;
        var name = props?.Name;
        if (string.IsNullOrWhiteSpace(name)) return null;

        // GeoJSON convention: [lon, lat].
        double lon = coords[0];
        double lat = coords[1];
        if (lat < -90.0 || lat > 90.0) return null;
        if (lon < -180.0 || lon > 180.0) return null;

        return new PlaceResult(
            Name: name,
            DisplayLabel: BuildDisplayLabel(name, props),
            Lat: lat,
            Lon: lon,
            Source: "photon");
    }

    /// <summary>
    /// "Name -- City, Country" with each piece optional. Photon
    /// fields are independently nullable, so a small rural place
    /// might have a name but no city; a country still anchors it.
    /// Type ("marina", "harbour", "city", ...) is rendered by the
    /// UI separately as a badge so it stays out of the label text.
    /// </summary>
    internal static string BuildDisplayLabel(string name, PhotonProperties? props)
    {
        var locality = props?.City ?? props?.State;
        var country = props?.Country;
        if (!string.IsNullOrWhiteSpace(locality) && !string.IsNullOrWhiteSpace(country))
            return $"{name} -- {locality}, {country}";
        if (!string.IsNullOrWhiteSpace(locality))
            return $"{name} -- {locality}";
        if (!string.IsNullOrWhiteSpace(country))
            return $"{name} -- {country}";
        return name;
    }
}
