using Microsoft.Extensions.Logging;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Services.Signalk;

/// <summary>
/// Resolves the self-vessel URN by hitting <c>/signalk/v1/api/self</c>.
/// The endpoint returns a single JSON string (the URN), which lets us
/// tag own-boat even when the websocket hello envelope never arrives
/// or the server only emits the prefixed-URN form on the delta stream.
///
/// <para>The resolved URN is handed back to <see cref="SignalkClient"/>
/// via the <c>setSelfContext</c> callback because the normalisation
/// rule (prefix with <c>vessels.</c> when missing) and the AIS-store
/// retro-eviction live there alongside the receive loop's own
/// hello-message branch. Keeping the callback narrow means this
/// class doesn't need a reference to <see cref="AisStore"/>.</para>
/// </summary>
public sealed class SignalkSelfContextSeeder
{
    private readonly HttpClient _http;
    private readonly ISignalKBaseUrl _baseUrl;
    private readonly ILogger<SignalkSelfContextSeeder> _logger;
    private readonly Action<string?> _setSelfContext;

    public SignalkSelfContextSeeder(
        HttpClient http,
        ISignalKBaseUrl baseUrl,
        ILogger<SignalkSelfContextSeeder> logger,
        Action<string?> setSelfContext)
    {
        _http = http;
        _baseUrl = baseUrl;
        _logger = logger;
        _setSelfContext = setSelfContext;
    }

    /// <summary>
    /// Fetch <c>/signalk/v1/api/self</c> and forward the trimmed URN
    /// to the <c>setSelfContext</c> callback. Best-effort: a non-2xx
    /// response is silent at Debug level; an unexpected exception is
    /// logged at Warning since a missing self URN noticeably degrades
    /// AIS filtering.
    /// </summary>
    /// <remarks>
    /// Response shape: a bare JSON string, e.g.
    /// <code>"vessels.urn:mrn:imo:mmsi:261006533"</code>
    /// or just <code>"urn:mrn:imo:mmsi:261006533"</code> -- the
    /// surrounding quotes are part of the JSON encoding and get
    /// stripped before the callback runs.
    /// </remarks>
    public async Task SeedAsync(CancellationToken ct)
    {
        try
        {
            var url = _baseUrl.Combine("/signalk/v1/api/self");
            using var res = await _http.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogDebug("self endpoint {Url} returned {Status}", url, (int)res.StatusCode);
                return;
            }
            // Server returns the URN as a JSON-encoded string; strip the
            // surrounding quotes so callers see the raw URN.
            var raw = (await res.Content.ReadAsStringAsync(ct)).Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(raw)) _setSelfContext(raw);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "REST self-context resolution failed");
        }
    }
}
