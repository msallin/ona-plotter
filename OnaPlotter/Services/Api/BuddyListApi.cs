using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OnaPlotter.Models;

namespace OnaPlotter.Services.Api;

/// <summary>HTTP client for sbender9/signalk-buddylist-plugin's REST
/// surface (<c>/plugins/signalk-buddylist-plugin/buddies</c>). Returns
/// the helm's current buddy set as SignalK contexts so
/// <see cref="OnaPlotter.Services.AisStore.UpdateBuddies"/> can stamp
/// the IsBuddy flag on every tracked vessel. Best-effort: returns null
/// when the plugin isn't installed or the request fails - the AIS
/// pipeline continues with an empty buddy set.</summary>
public sealed class BuddyListApi : IBuddyListApi
{
    private readonly HttpClient _http;
    private readonly ISignalKBaseUrl _baseUrl;
    private readonly ILogger<BuddyListApi> _logger;

    // Detection cache. Null = unknown; set once on first probe.
    private bool? _available;
    // Serialises concurrent first-probe callers so we only send one request.
    private readonly SemaphoreSlim _probeLock = new(1, 1);

    public BuddyListApi(HttpClient http, ISignalKBaseUrl baseUrl, ILogger<BuddyListApi> logger)
    {
        _http = http;
        _baseUrl = baseUrl;
        _logger = logger;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (_available is { } cached) return cached;
        await _probeLock.WaitAsync(ct);
        try
        {
            if (_available is { } cached2) return cached2;
            _ = await GetAllAsync(ct); // side-effect: fills the cache
            return _available ?? false;
        }
        finally { _probeLock.Release(); }
    }

    public async Task<IReadOnlyList<SignalkBuddy>?> GetAllAsync(CancellationToken ct = default)
    {
        var url = _baseUrl.Combine(SignalKUrls.BuddiesPath);
        try
        {
            using var res = await _http.GetAsync(url, ct);
            if (res.StatusCode == HttpStatusCode.NotFound)
            {
                _available = false;
                return null;
            }
            res.EnsureSuccessStatusCode();
            _available = true;

            // The plugin returns an object keyed by URN:
            //   { "urn:mrn:imo:mmsi:1234": { "name": "Friend boat" }, ... }
            var dict = await res.Content.ReadFromJsonAsync<Dictionary<string, JsonElement>>(cancellationToken: ct);
            if (dict is null) return Array.Empty<SignalkBuddy>();

            var list = new List<SignalkBuddy>(dict.Count);
            foreach (var (urn, body) in dict)
            {
                string? name = null;
                if (body.ValueKind == JsonValueKind.Object
                    && body.TryGetProperty("name", out var n)
                    && n.ValueKind == JsonValueKind.String)
                {
                    name = n.GetString();
                }
                list.Add(new SignalkBuddy(urn, name ?? urn));
            }
            return list;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Buddy list probe failed; treating plugin as not installed");
            _available = false;
            return null;
        }
    }

    public void Invalidate() => _available = null;

    public async Task<ApiResult> AddAsync(string urn, string name, CancellationToken ct = default)
    {
        var url = _baseUrl.Combine(SignalKUrls.BuddiesPath);
        try
        {
            using var res = await _http.PostAsJsonAsync(url, new { urn, name }, ct);
            if (res.IsSuccessStatusCode) { _available = true; return ApiResult.Ok; }
            if (res.StatusCode == HttpStatusCode.NotFound)
            {
                _available = false;
                return ApiResult.Fail("buddy-list plugin not installed");
            }
            _logger.LogWarning("Buddy add rejected with {Status}", res.StatusCode);
            return ApiResult.Fail($"HTTP {(int)res.StatusCode}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Buddy add failed");
            return ApiResult.Fail(ex.Message);
        }
    }

    public async Task<ApiResult> RemoveAsync(string urn, CancellationToken ct = default)
    {
        var url = _baseUrl.Combine(SignalKUrls.Buddy(urn));
        try
        {
            using var res = await _http.DeleteAsync(url, ct);
            if (res.IsSuccessStatusCode) { _available = true; return ApiResult.Ok; }
            if (res.StatusCode == HttpStatusCode.NotFound)
            {
                _available = false;
                return ApiResult.Fail("buddy-list plugin not installed");
            }
            _logger.LogWarning("Buddy delete rejected with {Status}", res.StatusCode);
            return ApiResult.Fail($"HTTP {(int)res.StatusCode}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Buddy delete failed");
            return ApiResult.Fail(ex.Message);
        }
    }
}
