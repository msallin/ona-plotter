using System.Net.Http;
using System.Text;
using System.Text.Json;
using OnaPlotter.Models;
using OnaPlotter.Services.Json;

namespace OnaPlotter.Services.Pois;

/// <summary>
/// Marine-POI provider backed by the public Overpass API. Default
/// endpoint is <c>https://overpass-api.de/api/interpreter</c>; the
/// constructor accepts an override for tests (or a future SignalK
/// server-side proxy plugin).
///
/// <para>Politeness: one in-flight request at a time. The public
/// Overpass instance documents a "no parallel requests, no bulk
/// pipelines" guideline; an interactive helm panning the chart fits
/// comfortably under that, but only if we don't queue up a fetch on
/// every intermediate moveend tick. The semaphore here, plus the
/// debounce in <see cref="OnaPlotter.Services.Map.MarinePoiController"/>,
/// is the rate limit. If a fetch is in flight when a new one arrives,
/// the new one waits.</para>
///
/// <para>Failure handling matches <see cref="IMarinePoiService"/>:
/// every transient error path returns an empty list. Logged at
/// warning so a deploy regression shows up in the SK server log via
/// <see cref="OnaPlotter.Services.ClientErrorRelay"/>, but never
/// throws into the controller (which would crash the layer toggle).</para>
/// </summary>
public sealed class OverpassPoiService : IMarinePoiService
{
    /// <summary>Public production endpoint. Override at construction
    /// for tests or a self-hosted instance. https-only.</summary>
    public const string DefaultEndpoint = "https://overpass-api.de/api/interpreter";

    /// <summary>Per-call HTTP cap. Slightly above the server-side
    /// timeout in <see cref="OverpassQueryBuilder.ServerTimeoutSeconds"/>
    /// so a slow-response Overpass request still surfaces cleanly
    /// rather than getting cut at 8 s by the ambient HttpClient
    /// timeout. Long enough for a worst-case bbox + tag set; short
    /// enough that a dead endpoint doesn't sit there for minutes.</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly ILogger<OverpassPoiService> _logger;
    private readonly TimeProvider _time;
    private readonly string _endpoint;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public OverpassPoiService(
        HttpClient http,
        ILogger<OverpassPoiService> logger,
        TimeProvider time,
        string endpoint = DefaultEndpoint)
    {
        _http = http;
        _logger = logger;
        _time = time;
        _endpoint = endpoint;
    }

    public async Task<IReadOnlyList<MarinePoi>> FetchAsync(
        double south, double west, double north, double east,
        IReadOnlySet<MarinePoiCategory> categories,
        CancellationToken ct = default)
    {
        var query = OverpassQueryBuilder.Build(south, west, north, east, categories);
        if (query is null) return [];

        // Linked CTS so an external cancel (helm panned again) wins
        // immediately, but the call also self-times-out at CallTimeout
        // if the endpoint is slow.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(CallTimeout);

        // Serialise on the gate so we never have two parallel Overpass
        // calls from the same client. Public-instance politeness
        // guideline; also prevents a helm rapidly toggling categories
        // from saturating the server. Wrap the WaitAsync too: a
        // pre-cancelled token (helm panned during the previous fetch
        // and we're now starting the next) throws OCE out of WaitAsync
        // itself, before the inner try / catch fires.
        try
        {
            await _gate.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        try
        {
            return await DoFetchAsync(query, timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<MarinePoi>> DoFetchAsync(string query, CancellationToken ct)
    {
        // Overpass takes the QL string in the POST body under the
        // "data" form field. Documented at the wiki link in
        // OverpassDtos.cs.
        using var content = new StringContent(
            "data=" + Uri.EscapeDataString(query),
            Encoding.UTF8,
            "application/x-www-form-urlencoded");

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync(_endpoint, content, ct);
        }
        catch (TaskCanceledException)
        {
            // External cancel (helm panned) or our timeout. Either way:
            // empty list, the next moveend tick will retry from cache
            // first and only fetch if the bbox actually changed.
            return [];
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("[marine-poi] overpass http failed: {Message}", ex.Message);
            return [];
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // 429 Too Many Requests is the most common failure on
                // the public endpoint; log at warn so a deploy regression
                // is visible without spamming.
                _logger.LogWarning("[marine-poi] overpass returned HTTP {Code}",
                    (int)response.StatusCode);
                return [];
            }

            OverpassResponse? parsed;
            try
            {
                var json = await response.Content.ReadAsStringAsync(ct);
                if (string.IsNullOrWhiteSpace(json)) return [];
                parsed = JsonSerializer.Deserialize(json, OnaJsonContext.Default.OverpassResponse);
            }
            catch (Exception ex) when (ex is JsonException
                                          or HttpRequestException
                                          or IOException)
            {
                _logger.LogWarning("[marine-poi] overpass read/parse failed: {Message}", ex.Message);
                return [];
            }
            catch (TaskCanceledException)
            {
                return [];
            }

            return OverpassResponseParser.Parse(parsed, _time.GetUtcNow().UtcDateTime);
        }
    }
}
