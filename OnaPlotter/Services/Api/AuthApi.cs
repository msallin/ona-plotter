using System.Text.Json;
using OnaPlotter.Models;
using OnaPlotter.Services.Json;

namespace OnaPlotter.Services.Api;

/// <summary>signalk-server's <c>/skServer/loginStatus</c> probe.
/// The path is server-implementation-specific (not in the SK spec)
/// but every signalk-server build ships it; third-party servers may
/// 404 and we degrade gracefully to "unknown" (null).
///
/// <para>Logging note: every failure path here is fully recoverable
/// (the chip retries on the next poll) so logs go through ILogger
/// at Warning / Information level rather than <c>Console.Error</c>.
/// The <c>errorRelayBoot.js</c> wrapper hooks <c>console.error</c>
/// only - routing handled-and-recoverable lines through ILogger
/// keeps them out of the SK server's relayed-unhandled-error stream
/// while still surfacing them in the helm's devtools console.</para></summary>
public sealed class AuthApi : IAuthApi
{
    /// <summary>Per-probe timeout. Short (admin endpoint should
    /// answer in &lt; 100 ms locally; even on a sat link 8 s is
    /// generous). Without this, the default HttpClient timeout is
    /// 100 s - the 5-min poll fires again at 300 s and pending
    /// probes stack until the helm's connection pool is full of
    /// auth requests competing with the SignalK websocket.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);

    private readonly HttpClient _http;
    private readonly ISignalKBaseUrl _baseUrl;
    private readonly ILogger<AuthApi> _logger;

    public AuthApi(HttpClient http, ISignalKBaseUrl baseUrl, ILogger<AuthApi> logger)
    {
        _http = http;
        _baseUrl = baseUrl;
        _logger = logger;
    }

    public async Task<LoginStatus?> GetLoginStatusAsync(CancellationToken ct = default)
    {
        // Path is /skServer/loginStatus - NOT under /signalk/. The
        // SK spec doesn't define an auth-status surface; signalk-
        // server's internal admin endpoint is the de-facto source.
        // BaseUrl strips the plugin mount (origin only), so this
        // hits the SK server even when OnaPlotter runs under a
        // /signalk-onaplotter/ webapp mount.
        var url = _baseUrl.Combine("/skServer/loginStatus");

        // Bound the call with a timeout linked to the caller's CT
        // so a slow / hanging server can't stack pending probes
        // against the next 5-min tick.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ProbeTimeout);

        HttpResponseMessage response;
        try { response = await _http.GetAsync(url, cts.Token); }
        catch (HttpRequestException ex)
        {
            // Network error: log so a 3 a.m. "the chip just won't
            // appear" diagnosis has something to grep for. Warning
            // level (not error) - the chip retries on the next tick.
            _logger.LogWarning("[auth] probe failed: {Message}", ex.Message);
            return null;
        }
        catch (TaskCanceledException)
        {
            // Either the caller cancelled or our 8 s timeout fired.
            // Both are "no signal this tick"; the next poll retries.
            // Information level - timeouts are routine on flaky LTE.
            _logger.LogInformation("[auth] probe timed out");
            return null;
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // 404 = third-party SK server without /skServer/loginStatus
                // (degrades to "unknown"); 401/403 = auth lapse caught
                // here too. Both are recoverable; warn rather than error.
                _logger.LogWarning("[auth] probe HTTP {StatusCode}", (int)response.StatusCode);
                return null;
            }
            var json = await response.Content.ReadAsStringAsync(cts.Token);
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                // Source-gen path. The context-level
                // PropertyNameCaseInsensitive=true preserves the
                // earlier per-call option, so a server that capitalises
                // "Status" or "AuthenticationRequired" still parses.
                return JsonSerializer.Deserialize(json, OnaJsonContext.Default.LoginStatus);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning("[auth] probe parse failed: {Message}", ex.Message);
                return null;
            }
        }
    }

    public async Task<LoginResult?> LoginAsync(string username, string password,
        CancellationToken ct = default)
    {
        var url = _baseUrl.Combine("/signalk/v1/auth/login");
        // Hand-rolled JSON body so we don't have to add the request
        // shape to OnaJsonContext (it's only used here, and only with
        // two fields). System.Text.Json escapes the values for us.
        var bodyJson = JsonSerializer.Serialize(new[]
        {
            new KeyValuePair<string, string>("username", username),
            new KeyValuePair<string, string>("password", password),
        }.ToDictionary(kv => kv.Key, kv => kv.Value));
        using var content = new StringContent(bodyJson, System.Text.Encoding.UTF8, "application/json");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ProbeTimeout);

        HttpResponseMessage response;
        try { response = await _http.PostAsync(url, content, cts.Token); }
        catch (HttpRequestException ex)
        {
            // Network / CORS / DNS - all surface as HttpRequestException
            // in the browser HTTP stack. Most common in standalone mode
            // is a CORS-block on the SK server (the helm hasn't added
            // this origin to security.allowedCorsOrigins yet); the helm
            // sees a "sign-in failed" toast and the next chip refresh
            // confirms they're still signed out.
            _logger.LogWarning("[auth] login failed: {Message}", ex.Message);
            return null;
        }
        catch (TaskCanceledException)
        {
            _logger.LogInformation("[auth] login timed out");
            return null;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[auth] login HTTP {StatusCode}", (int)response.StatusCode);
                return null;
            }
            var json = await response.Content.ReadAsStringAsync(cts.Token);
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                return JsonSerializer.Deserialize(json, OnaJsonContext.Default.LoginResult);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning("[auth] login parse failed: {Message}", ex.Message);
                return null;
            }
        }
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        var url = _baseUrl.Combine("/signalk/v1/auth/logout");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ProbeTimeout);
        try
        {
            using var response = await _http.PostAsync(url, content: null, cts.Token);
            // Don't surface non-success - the caller has already cleared
            // the local token; the server-side invalidation is best-
            // effort, the JWT will time out within timeToLive anyway.
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation("[auth] logout HTTP {StatusCode}", (int)response.StatusCode);
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogInformation("[auth] logout transport failed: {Message}", ex.Message);
        }
        catch (TaskCanceledException)
        {
            _logger.LogInformation("[auth] logout timed out");
        }
    }
}
