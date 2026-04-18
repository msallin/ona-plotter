using OnaPlotter.Models;

namespace OnaPlotter.Services.Api;

/// <summary>CRUD for SignalK notes -- short geolocated text annotations
/// (the boat-world equivalent of a Google-Maps pushpin note).</summary>
public interface INoteApi
{
    Task<List<SignalkNote>> GetAllAsync(CancellationToken ct = default);
    Task<string?> CreateAsync(string title, string description, double lat, double lon, CancellationToken ct = default);
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);
}
