using OnaPlotter.Models;

namespace OnaPlotter.Services.Api;

/// <summary>
/// SignalK notes client. Unlike routes/waypoints, the note resource uses
/// a bare <c>{title, description, position: {latitude, longitude}}</c>
/// shape with no Feature/properties wrapper; see the upstream Note
/// definition in sk-types. Peer SignalK clients read the same
/// endpoint.
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

    public Task<ApiResult<string>> CreateAsync(string title, string description, double lat, double lon, CancellationToken ct = default)
    {
        // Stamp createdAt on first PUT so the note popup can show
        // "when did this appear" without standing up a sidecar
        // resource. ISO-8601 UTC ("o" format) round-trips through
        // System.Text.Json's DateTime parser unambiguously. Notes
        // authored by peer SignalK clients won't carry this and the
        // UI renders a dash; that's the honest "we don't know".
        var body = new
        {
            title,
            description,
            position = new { latitude = lat, longitude = lon },
            createdAt = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
        };
        var url = _baseUrl.Combine(SignalKUrls.NotesPath);
        return ResourceHttp.PostCreateAsync(_http, url, body, ct);
    }

    public Task<ApiResult> UpdateAsync(SignalkNote note, string title, string? description, CancellationToken ct = default)
    {
        // Re-PUT the full note shape: SignalK's resources-fs provider
        // replaces the whole resource on PUT (no PATCH). Position must
        // round-trip from the existing note so the helm doesn't lose
        // the anchor when they only meant to fix a typo.
        // CreatedAt similarly round-trips so an Edit doesn't reset
        // the "first seen" timestamp - the UI is "when was this
        // pinned", not "when did the title change".
        if (note.Position is null) return Task.FromResult(ApiResult.Fail("note has no position"));
        var body = new
        {
            title,
            description,
            position = new
            {
                latitude = note.Position.Latitude,
                longitude = note.Position.Longitude,
            },
            createdAt = note.CreatedAt?.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
        };
        var url = _baseUrl.Combine(SignalKUrls.Note(note.Id));
        return ResourceHttp.PutAsync(_http, url, body, ct);
    }

    public Task<ApiResult> DeleteAsync(string id, CancellationToken ct = default) =>
        ResourceHttp.DeleteAsync(_http, _baseUrl.Combine(SignalKUrls.Note(id)), ct);
}
