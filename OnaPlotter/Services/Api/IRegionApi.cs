using OnaPlotter.Models;

namespace OnaPlotter.Services.Api;

/// <summary>CRUD for SignalK regions (polygonal areas on the chart).
/// Creation via this client is limited to circle-approximating
/// polygons; Freeboard-compatible polygon drawing is a future phase.</summary>
public interface IRegionApi
{
    Task<List<SignalkRegion>> GetAllAsync(CancellationToken ct = default);
    Task<string?> CreateCircleAsync(string name, string description,
        double lat, double lon, double radiusMeters, CancellationToken ct = default);
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);
}
