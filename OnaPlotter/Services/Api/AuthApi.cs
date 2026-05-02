using System.Text.Json;
using OnaPlotter.Models;
using OnaPlotter.Services.Json;

namespace OnaPlotter.Services.Api;

/// <summary>signalk-server's <c>/skServer/loginStatus</c> probe.
/// The path is server-implementation-specific (not in the SK spec)
/// but every signalk-server build ships it; third-party servers may
/// 404 and we degrade gracefully to "unknown" (null).</summary>
public sealed class AuthApi : IAuthApi
{
    /// <summary>Per-probe timeout. Short (admin endpoint should
    /// answer in &lt; 100 ms locally; even on a sat link 8 s is
    /// generous). Without this, the default HttpClient timeout is
    /// 100 s -- the 5-min poll fires again at 300 s and pending
    /// probes stack until the helm's connection pool is full of
    /// auth requests competing with the SignalK websocket.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);

    private readonly HttpClient _http;
    private readonly ISignalKBaseUrl _baseUrl;

    public AuthApi(HttpClient http, ISignalKBaseUrl baseUrl)
    {
        _http = http;
        _baseUrl = baseUrl;
    }

    public async Task<LoginStatus?> GetLoginStatusAsync(CancellationToken ct = default)
    {
        // Path is /skServer/loginStatus -- NOT under /signalk/. The
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
            // Log so a 3 a.m. "the chip just won't appear" diagnosis
            // has something to grep for. No PII risk -- the URL is
            // the local SK server and the message is the network
            // error.
            Console.Error.WriteLine($"[auth] probe failed: {ex.Message}");
            return null;
        }
        catch (TaskCanceledException)
        {
            // Either the caller cancelled or our 8 s timeout fired.
            // Both are "no signal this tick"; the next poll retries.
            Console.Error.WriteLine("[auth] probe timed out");
            return null;
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"[auth] probe HTTP {(int)response.StatusCode}");
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
                Console.Error.WriteLine($"[auth] probe parse failed: {ex.Message}");
                return null;
            }
        }
    }
}
