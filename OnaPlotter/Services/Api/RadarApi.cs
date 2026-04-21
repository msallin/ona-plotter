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

            // Two shapes; one HTTP hit, parse the JSON once and branch.
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return ParseRadarList(doc.RootElement);
        }
    }

    // Extracted so a unit test can feed it sample JSON from the spec
    // without needing an HttpClient harness.
    internal static List<RadarInfo> ParseRadarList(JsonElement root)
    {
        var list = new List<RadarInfo>();
        switch (root.ValueKind)
        {
            case JsonValueKind.Array:
                // Current openplotter shape: [{id, name, brand, ...}, ...]
                foreach (var el in root.EnumerateArray())
                {
                    var info = el.Deserialize<RadarInfo>(s_json);
                    if (info is null) continue;
                    // Server included id inline; keep it.
                    list.Add(info);
                }
                break;
            case JsonValueKind.Object:
                // Spec shape: { "nav1034A": {...}, "nav1034B": {...} }.
                // Per-entry id is the dict key; copy it onto the DTO
                // so downstream code doesn't have to thread the key.
                foreach (var prop in root.EnumerateObject())
                {
                    var info = prop.Value.Deserialize<RadarInfo>(s_json);
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

    public async Task<bool> SetControlAsync(string radarId, string controlId, ControlValue value, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(radarId) || string.IsNullOrEmpty(controlId)) return false;
        var url = _baseUrl.Combine(SignalKUrls.RadarControl(radarId, controlId));
        try
        {
            using var response = await _http.PutAsJsonAsync(url, value, s_json, ct);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
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
