using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OnaPlotter.Models;

namespace OnaPlotter.Services.Api;

public sealed class BuddyListApi : IBuddyListApi
{
    private readonly HttpClient _http;
    private readonly ISignalKBaseUrl _baseUrl;
    private readonly ILogger<BuddyListApi> _logger;

    // Detection cache. Null = unknown; set once on first probe.
    private bool? _available;

    public BuddyListApi(HttpClient http, ISignalKBaseUrl baseUrl, ILogger<BuddyListApi> logger)
    {
        _http = http;
        _baseUrl = baseUrl;
        _logger = logger;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (_available is { } cached) return cached;
        _ = await GetAllAsync(ct); // side-effect: fills the cache
        return _available ?? false;
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

    public void InvalidateAsync() => _available = null;
}
