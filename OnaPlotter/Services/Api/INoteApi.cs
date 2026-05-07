using OnaPlotter.Models;

namespace OnaPlotter.Services.Api;

/// <summary>CRUD for SignalK notes - short geolocated text annotations
/// (the boat-world equivalent of a Google-Maps pushpin note).</summary>
public interface INoteApi
{
    Task<List<SignalkNote>> GetAllAsync(CancellationToken ct = default);
    Task<ApiResult<string>> CreateAsync(string title, string description, double lat, double lon, CancellationToken ct = default);
    /// <summary>PUT a new title / description on an existing note,
    /// preserving its position. Used by the Layers-panel Edit button
    /// (rename-style). The position is intentionally not editable
    /// here - relocating a note is rarer than renaming and keeping
    /// the body small avoids a second prompt the helm has to dismiss.</summary>
    Task<ApiResult> UpdateAsync(SignalkNote note, string title, string? description, CancellationToken ct = default);
    Task<ApiResult> DeleteAsync(string id, CancellationToken ct = default);
}
