using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using OnaPlotter.Services.Json;

namespace OnaPlotter.Services.Places;

/// <summary>
/// Nominatim (OpenStreetMap) geocoder client. Used as the
/// <see cref="FallbackPlaceSearchService"/> backup behind Photon: if
/// Photon is rate-limited, down, or returns nothing for a query
/// Nominatim might still answer.
///
/// <para>Nominatim usage policy
/// (<c>https://operations.osmfoundation.org/policies/nominatim/</c>)
/// mandates two things this client honours:</para>
/// <list type="number">
///   <item><description>An identifying <b>User-Agent</b> header. We
///     send <c>OnaPlotter/1.0 (+https://github.com/msallin/ona-plotter)</c>
///     so the OSMF can reach the project if there's an issue.</description></item>
///   <item><description>Max <b>1 request per second</b>. The fallback
///     decorator only calls us when Photon misses and the merging
///     layer caches non-empty results, so steady-state load on a
///     single helm is far below 1 rps; the rate limiter in this
///     class is belt-and-braces in case a debounce flutter or a
///     burst of misses bunches up several calls in the same
///     second.</description></item>
/// </list>
///
/// <para>All transient failure paths (timeout, network, non-2xx,
/// malformed JSON) return an empty list to honour the
/// <see cref="IPlaceSearchService"/> contract; the cache decorator
/// won't poison its store with an empty hit, so the next call after
/// connectivity returns will re-fetch.</para>
/// </summary>
public sealed class NominatimPlaceSearchService : IPlaceSearchService
{
    /// <summary>Result limit on the Nominatim URL. Same as
    /// <see cref="PhotonPlaceSearchService.ResultLimit"/> so the
    /// fallback dropdown has consistent depth.</summary>
    public const int ResultLimit = 10;

    /// <summary>Per-call HTTP cap. Nominatim's public instance is
    /// best-effort and slower than Photon under load; budget the
    /// same 4 s as Photon so the fallback path is bounded at
    /// roughly twice the primary's worst case (Photon timeout +
    /// Nominatim timeout).</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(4);

    /// <summary>OSMF policy is &lt;= 1 rps. We enforce a 1.1 s
    /// minimum interval between two outbound requests to leave a
    /// little headroom against clock drift.</summary>
    public static readonly TimeSpan MinRequestInterval = TimeSpan.FromMilliseconds(1100);

    /// <summary>User-Agent string Nominatim requires. Includes the
    /// project name + a contactable URL; updating the version is a
    /// no-op for the API but useful for OSMF traffic-shaping
    /// telemetry.</summary>
    internal const string UserAgent = "OnaPlotter/1.0 (+https://github.com/msallin/ona-plotter)";

    private const string EndpointBase = "https://nominatim.openstreetmap.org/search";

    private readonly HttpClient _http;
    private readonly ILogger<NominatimPlaceSearchService> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _rateGate = new(1, 1);
    private long _lastRequestTicks;

    public NominatimPlaceSearchService(
        HttpClient http,
        ILogger<NominatimPlaceSearchService> logger,
        TimeProvider? time = null)
    {
        _http = http;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<PlaceResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        // Linked CTS: external cancel (helm typed another char) wins
        // immediately; otherwise we self-time-out at CallTimeout.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(CallTimeout);

        // Wait for our turn under the 1 rps gate. Cancellation through
        // the gate is honoured (helm typed another char before we got
        // the slot) so we don't block the next caller behind a stale
        // request.
        try { await _rateGate.WaitAsync(timeoutCts.Token); }
        catch (OperationCanceledException)
        {
            return [];
        }

        try
        {
            // Sleep just long enough so the call lands at least
            // MinRequestInterval after the previous one. First call
            // (lastTicks == 0) skips the wait entirely.
            //
            // Task.Delay is routed through the injected TimeProvider so
            // the rate-gate test can advance a fake clock instead of
            // waiting on a real timer. Without the TimeProvider overload
            // a system-timer Delay would race the test's clock-advance
            // and force the test to insert a real Task.Delay(N) sleep
            // to bridge the two -- which is exactly the kind of CI flake
            // vector the FakeTimeProvider seam exists to eliminate.
            var nowTicks = _time.GetUtcNow().UtcTicks;
            var elapsed = TimeSpan.FromTicks(nowTicks - _lastRequestTicks);
            if (_lastRequestTicks != 0 && elapsed < MinRequestInterval)
            {
                var wait = MinRequestInterval - elapsed;
                // Catch the BASE OperationCanceledException, not just
                // TaskCanceledException -- the TimeProvider Task.Delay
                // overload can surface either depending on runtime.
                try { await Task.Delay(wait, _time, timeoutCts.Token); }
                catch (OperationCanceledException) { return []; }
            }
            _lastRequestTicks = _time.GetUtcNow().UtcTicks;

            var url = EndpointBase
                + "?q=" + Uri.EscapeDataString(query)
                + "&format=jsonv2"
                + "&limit=" + ResultLimit;

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // Per OSMF policy: identify the client. Browsers strip
            // / override the User-Agent for fetch on most platforms,
            // but Blazor WASM's HttpClient on .NET 10 does pass
            // custom request-level headers through to the underlying
            // browser fetch. If a future runtime drops it the call
            // still works (Nominatim degrades to 403, we return
            // empty) so this is a best-effort compliance hint, not a
            // hard requirement.
            request.Headers.UserAgent.ParseAdd(UserAgent);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, timeoutCts.Token);
            }
            catch (TaskCanceledException)
            {
                _logger.LogInformation("[place] nominatim timed out / cancelled for query '{Query}'", query);
                return [];
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning("[place] nominatim http failed for query '{Query}': {Message}",
                    query, ex.Message);
                return [];
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("[place] nominatim returned HTTP {StatusCode} for query '{Query}'",
                        (int)response.StatusCode, query);
                    return [];
                }

                NominatimResult[]? parsed;
                try
                {
                    var json = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                    if (string.IsNullOrWhiteSpace(json)) return [];
                    parsed = JsonSerializer.Deserialize(json, OnaJsonContext.Default.NominatimResultArray);
                }
                // Same widened-catch as Photon: a body-stream drop
                // mid-read on flaky LTE surfaces as HttpRequestException
                // / IOException, not JsonException. Without these the
                // contract leaks through into SearchBox.
                catch (Exception ex) when (ex is JsonException
                                              or HttpRequestException
                                              or IOException)
                {
                    _logger.LogWarning("[place] nominatim read/parse failed for query '{Query}': {Message}",
                        query, ex.Message);
                    return [];
                }
                catch (TaskCanceledException)
                {
                    return [];
                }

                if (parsed is null || parsed.Length == 0) return [];

                var results = new List<PlaceResult>(parsed.Length);
                foreach (var row in parsed)
                {
                    if (TryMapResult(row) is { } mapped) results.Add(mapped);
                }
                return results;
            }
        }
        finally
        {
            _rateGate.Release();
        }
    }

    /// <summary>
    /// Convert one Nominatim row to a dropdown <see cref="PlaceResult"/>.
    /// Returns null when the row lacks parseable coords / a name we
    /// can render. Nominatim emits lat/lon as strings, so we parse with
    /// <see cref="CultureInfo.InvariantCulture"/> regardless of the
    /// ambient locale.
    /// </summary>
    internal static PlaceResult? TryMapResult(NominatimResult? row)
    {
        if (row is null) return null;

        if (!double.TryParse(row.Lat, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat))
            return null;
        if (!double.TryParse(row.Lon, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
            return null;
        if (!double.IsFinite(lat) || !double.IsFinite(lon)) return null;
        if (lat < -90.0 || lat > 90.0) return null;
        if (lon < -180.0 || lon > 180.0) return null;

        // Nominatim's `name` is sometimes empty even for a real place
        // (e.g. small admin boundaries) -- fall back to the head of
        // display_name so the row still has a label.
        string? name = !string.IsNullOrWhiteSpace(row.Name) ? row.Name : null;
        if (name is null && !string.IsNullOrWhiteSpace(row.DisplayName))
        {
            var head = row.DisplayName.Split(',', 2)[0].Trim();
            if (!string.IsNullOrEmpty(head)) name = head;
        }
        if (string.IsNullOrWhiteSpace(name)) return null;

        var label = !string.IsNullOrWhiteSpace(row.DisplayName) ? row.DisplayName : name;

        return new PlaceResult(
            Name: name,
            DisplayLabel: label,
            Lat: lat,
            Lon: lon,
            Source: "nominatim");
    }
}
