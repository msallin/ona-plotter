using System.Text.Json;
using OnaPlotter.Models;

namespace OnaPlotter.Services.Api;

/// <summary>
/// Signal K Radar API v3.1 client. Server responses follow two shapes
/// depending on implementation age - the spec says "dict keyed by
/// radar id" on <c>GET /radars</c>, but the reference implementation
/// (mayara-server) currently returns a plain array with <c>id</c>
/// baked into each entry. We accept both.
/// </summary>
public sealed class RadarApi : IRadarApi
{
    private readonly HttpClient _http;
    private readonly ISignalKBaseUrl _baseUrl;
    private readonly ILogger<RadarApi> _logger;

    // One reusable options bag. The legend-color converter is
    // registered on the DTOs via attribute; no options-level setup
    // required. WhenWritingNull keeps PUT bodies (control writes)
    // minimal - ControlValue's optional sector / zone / rect fields
    // would otherwise serialise as "auto":null,"endValue":null,...
    // and Mayara rejects the bloated body with HTTP 400.
    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public RadarApi(HttpClient http, ISignalKBaseUrl baseUrl, ILogger<RadarApi> logger)
    {
        _http = http;
        _baseUrl = baseUrl;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RadarInfo>> GetAllAsync(CancellationToken ct = default)
    {
        // A missing plugin (404), an unreachable server or a malformed
        // body all mean "no radars visible right now" rather than an
        // error toast; the UI polls again later.
        var result = await ResourceHttp.GetJsonAsync<IReadOnlyList<RadarInfo>>(
            _http, _baseUrl.Combine(SignalKUrls.RadarsPath), _logger, ParseRadarList, ct);
        return result.Success && result.Value is not null ? result.Value : [];
    }

    // Internal for unit testing; production callers use GetAllAsync.
    // Three input shapes in the wild; we normalise to the same flat
    // list regardless of how the server chose to frame things:
    //   (A) Array:   [{id, name, brand, ...}, ...]           (mayara / openplotter today)
    //   (B) Dict:    { "nav1034A": {...}, "nav1034B": {...}} (radar_api.md REST example)
    //   (C) Wrapped: { "version": "3.1", "radars": { ... } } (radar_api.md TypeScript spec)
    // The wrapped form nests either form (A) or (B) under "radars".
    internal static IReadOnlyList<RadarInfo> ParseRadarList(JsonElement root)
    {
        // Unwrap the versioned envelope if present, then dispatch on
        // the shape of the payload. Missing "radars" property falls
        // through to the object branch below and yields no entries,
        // which is harmless.
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("radars", out var inner) &&
            (inner.ValueKind == JsonValueKind.Object || inner.ValueKind == JsonValueKind.Array))
        {
            root = inner;
        }

        var list = new List<RadarInfo>();
        switch (root.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var el in root.EnumerateArray())
                {
                    var info = el.Deserialize<RadarInfo>(s_json);
                    if (info is null) continue;
                    list.Add(info);
                }
                break;
            case JsonValueKind.Object:
                // Per-entry id is the dict key; copy it onto the DTO
                // so downstream code doesn't have to thread the key.
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                    RadarInfo? info;
                    try { info = prop.Value.Deserialize<RadarInfo>(s_json); }
                    catch (JsonException) { continue; }
                    if (info is null) continue;
                    if (string.IsNullOrEmpty(info.Id)) info.Id = prop.Name;
                    list.Add(info);
                }
                break;
        }
        return list;
    }

    public async Task<RadarCapabilities?> GetCapabilitiesAsync(string radarId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(radarId)) return null;
        var result = await ResourceHttp.GetJsonAsync<RadarCapabilities>(
            _http, _baseUrl.Combine(SignalKUrls.RadarCapabilities(radarId)), _logger, ct, s_json);
        return result.Success ? result.Value : null;
    }

    public async Task<Dictionary<string, ControlValue>?> GetControlsAsync(string radarId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(radarId)) return null;
        var result = await ResourceHttp.GetJsonAsync<Dictionary<string, ControlValue>>(
            _http, _baseUrl.Combine(SignalKUrls.RadarControls(radarId)), _logger, ct, s_json);
        return result.Success ? result.Value : null;
    }

    /// <summary>Writes one control. On rejection the server answers
    /// <c>{"success":false,"error":"...","controlId":"range"}</c>;
    /// <see cref="ResourceHttp"/> surfaces the <c>error</c> field as
    /// <see cref="ApiResult.Error"/>.</summary>
    public Task<ApiResult> SetControlAsync(string radarId, string controlId, ControlValue value, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(radarId) || string.IsNullOrEmpty(controlId))
            return Task.FromResult(ApiResult.Fail("missing radar or control id"));
        var url = _baseUrl.Combine(SignalKUrls.RadarControl(radarId, controlId));
        return ResourceHttp.PutAsync(_http, url, value, ct, s_json);
    }
}
