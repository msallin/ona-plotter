using System.Net;
using System.Text.Json;
using OnaPlotter.Models;

namespace OnaPlotter.Services.Api;

/// <summary>HTTP client for sbender9/signalk-buddylist-plugin's REST
/// surface (<c>/plugins/signalk-buddylist-plugin/buddies</c>). Returns
/// the helm's current buddy set as SignalK contexts so
/// <see cref="OnaPlotter.Services.AisStore.UpdateBuddies"/> can stamp
/// the IsBuddy flag on every tracked vessel. Best-effort: returns null
/// when the plugin isn't installed or the request fails - the AIS
/// pipeline continues with an empty buddy set. Transport and error
/// policy is <see cref="ResourceHttp"/>'s; this class only adds the
/// installed / not-installed cache behind <see cref="IsAvailableAsync"/>.</summary>
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
        // The plugin returns an object keyed by URN:
        //   { "urn:mrn:imo:mmsi:1234": { "name": "Friend boat" }, ... }
        var result = await ResourceHttp.GetJsonAsync<Dictionary<string, JsonElement>>(
            _http, _baseUrl.Combine(SignalKUrls.BuddiesPath), _logger, ct, JsonSerializerOptions.Web);
        if (!result.Success)
        {
            NoteAvailability(result.StatusCode);
            return null;
        }
        _available = true;
        if (result.Value is null) return Array.Empty<SignalkBuddy>();

        var list = new List<SignalkBuddy>(result.Value.Count);
        foreach (var (urn, body) in result.Value)
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

    public void Invalidate() => _available = null;

    public async Task<ApiResult> AddAsync(string urn, string name, CancellationToken ct = default)
    {
        var result = await ResourceHttp.PostAsync(
            _http, _baseUrl.Combine(SignalKUrls.BuddiesPath), new { urn, name }, ct);
        return MapWriteResult(result, "add");
    }

    public async Task<ApiResult> RemoveAsync(string urn, CancellationToken ct = default)
    {
        var result = await ResourceHttp.DeleteAsync(_http, _baseUrl.Combine(SignalKUrls.Buddy(urn)), ct);
        return MapWriteResult(result, "delete");
    }

    /// <summary>A 2xx proves the plugin is installed; a 404 proves it
    /// isn't. Any other failure (transport, timeout, 5xx, 401) says
    /// nothing about installation, so the cache stays as it was and
    /// the next probe asks again.</summary>
    private void NoteAvailability(int? failedStatus)
    {
        if (failedStatus == (int)HttpStatusCode.NotFound) _available = false;
    }

    private ApiResult MapWriteResult(ApiResult result, string verb)
    {
        if (result.Success)
        {
            _available = true;
            return result;
        }
        NoteAvailability(result.StatusCode);
        if (result.StatusCode == (int)HttpStatusCode.NotFound)
            return ApiResult.Fail("buddy-list plugin not installed", result.StatusCode);
        _logger.LogWarning("Buddy {Verb} failed: {Error} (HTTP {Status})", verb, result.Error, result.StatusCode);
        return result;
    }
}
