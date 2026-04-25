using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OnaPlotter.Models;

namespace OnaPlotter.Services.Api;

/// <summary>
/// Signal K Radar API v3.1 client. Server responses follow two shapes
/// depending on implementation age -- the spec says "dict keyed by
/// radar id" on <c>GET /radars</c>, but the reference implementation
/// (mayara-server) currently returns a plain array with <c>id</c>
/// baked into each entry. We accept both.
/// </summary>
public sealed class RadarApi : IRadarApi
{
    private readonly HttpClient _http;
    private readonly ISignalKBaseUrl _baseUrl;

    // One reusable options bag. The legend-color converter is
    // registered on the DTOs via attribute; no options-level setup
    // required.
    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public RadarApi(HttpClient http, ISignalKBaseUrl baseUrl)
    {
        _http = http;
        _baseUrl = baseUrl;
    }

    public async Task<IReadOnlyList<RadarInfo>> GetAllAsync(CancellationToken ct = default)
    {
        var url = _baseUrl.Combine(SignalKUrls.RadarsPath);
        HttpResponseMessage response;
        try { response = await _http.GetAsync(url, ct); }
        catch (HttpRequestException) { return []; }        // server / plugin missing
        using (response)
        {
            if (!response.IsSuccessStatusCode) return [];

            // Three shapes in the wild; parse the JSON once and branch.
            // A malformed body just means "no radars visible right now"
            // rather than an error toast -- the UI polls again later.
            try
            {
                using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                return ParseRadarList(doc.RootElement);
            }
            catch (JsonException) { return []; }
        }
    }

    // Internal for unit testing; production callers use GetAllAsync.
    // Three input shapes in the wild; we normalise to the same flat
    // list regardless of how the server chose to frame things:
    //   (A) Array:   [{id, name, brand, ...}, ...]           (mayara / openplotter today)
    //   (B) Dict:    { "nav1034A": {...}, "nav1034B": {...}} (radar_api.md REST example)
    //   (C) Wrapped: { "version": "3.1", "radars": { ... } } (radar_api.md TypeScript spec)
    // The wrapped form nests either form (A) or (B) under "radars".
    internal static List<RadarInfo> ParseRadarList(JsonElement root)
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
        var url = _baseUrl.Combine(SignalKUrls.RadarCapabilities(radarId));
        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<RadarCapabilities>(s_json, ct);
        }
        catch (HttpRequestException) { return null; }
        catch (JsonException) { return null; }             // malformed / mid-upgrade
    }

    public async Task<Dictionary<string, ControlValue>?> GetControlsAsync(string radarId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(radarId)) return null;
        var url = _baseUrl.Combine(SignalKUrls.RadarControls(radarId));
        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<Dictionary<string, ControlValue>>(s_json, ct);
        }
        catch (HttpRequestException) { return null; }
        catch (JsonException) { return null; }
    }

    public async Task<ApiResult> SetControlAsync(string radarId, string controlId, ControlValue value, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(radarId) || string.IsNullOrEmpty(controlId))
            return ApiResult.Fail("missing radar or control id");
        var url = _baseUrl.Combine(SignalKUrls.RadarControl(radarId, controlId));
        try
        {
            using var response = await _http.PutAsJsonAsync(url, value, s_json, ct);
            if (response.IsSuccessStatusCode) return ApiResult.Ok;
            return ApiResult.Fail(await ReadErrorBodyAsync(response, ct)
                ?? $"HTTP {(int)response.StatusCode}");
        }
        catch (HttpRequestException ex) { return ApiResult.Fail(ex.Message); }
    }

    /// <summary>
    /// Parses the server's failure envelope (<c>{"success":false,
    /// "error":"...","controlId":"range"}</c>) and returns the
    /// <c>error</c> field when present. Falls back to the raw body
    /// (truncated) on malformed JSON, or null if reading the body
    /// itself throws. Centralised so other endpoints can adopt the
    /// same error-surfacing path.
    /// </summary>
    private static async Task<string?> ReadErrorBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(body)) return null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("error", out var e) &&
                    e.ValueKind == JsonValueKind.String)
                {
                    return e.GetString();
                }
            }
            catch (JsonException)
            {
                return body.Length > 200 ? body[..200] : body;
            }
        }
        catch { /* body read failed -- caller falls back to status code */ }
        return null;
    }

    public async Task<IReadOnlyList<RadarArpaTarget>?> GetTargetsAsync(string radarId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(radarId)) return null;
        var url = _baseUrl.Combine(SignalKUrls.RadarTargets(radarId));
        try
        {
            using var response = await _http.GetAsync(url, ct);
            // 501 is the spec signal for "provider doesn't support
            // ARPA". Surface as null so the UI can hide the targets
            // row rather than toast a transient error.
            if (response.StatusCode == HttpStatusCode.NotImplemented) return null;
            if (!response.IsSuccessStatusCode) return [];
            var list = await response.Content.ReadFromJsonAsync<List<RadarArpaTarget>>(s_json, ct);
            return list ?? [];
        }
        catch (HttpRequestException) { return []; }
        catch (JsonException) { return []; }
    }
}
