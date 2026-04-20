using System.Net.Http.Json;
using OnaPlotter.Models;

namespace OnaPlotter.Services.Api;

/// <summary>
/// SignalK notes client. Unlike routes/waypoints, the note resource uses
/// a bare <c>{title, description, position: {latitude, longitude}}</c>
/// shape with no Feature/properties wrapper; see the upstream Note
/// definition in sk-types. Freeboard-SK reads the same endpoint.
/// </summary>
public sealed class NoteApi : INoteApi
{
    private readonly HttpClient _http;
    private readonly ISignalKBaseUrl _baseUrl;

    public NoteApi(HttpClient http, ISignalKBaseUrl baseUrl)
    {
        _http = http;
        _baseUrl = baseUrl;
    }

    public async Task<List<SignalkNote>> GetAllAsync(CancellationToken ct = default)
    {
        var dict = await ResourceHttp.GetDictAsync<SignalkNote>(
            _http, _baseUrl.Combine(SignalKUrls.NotesPath), ct);
        if (dict is null) return [];

        var notes = new List<SignalkNote>(dict.Count);
        foreach (var (key, note) in dict)
        {
            // Drop notes without a renderable position (they might use
            // region or geohash references, which we don't consume yet).
            if (note.Position is null) continue;
            note.Id = key;
            notes.Add(note);
        }
        return notes;
    }

    public async Task<string?> CreateAsync(string title, string description, double lat, double lon, CancellationToken ct = default)
    {
        var body = new
        {
            title,
            description,
            position = new { latitude = lat, longitude = lon }
        };
        var url = _baseUrl.Combine(SignalKUrls.NotesPath);
        using var response = await _http.PostAsJsonAsync(url, body, ct);
        if (!response.IsSuccessStatusCode) return null;

        var result = await response.Content.ReadAsStringAsync(ct);
        return ResourceHttp.ParseCreatedId(result);
    }

    public Task<bool> DeleteAsync(string id, CancellationToken ct = default) =>
        ResourceHttp.DeleteAsync(_http, _baseUrl.Combine(SignalKUrls.Note(id)), ct);
}
