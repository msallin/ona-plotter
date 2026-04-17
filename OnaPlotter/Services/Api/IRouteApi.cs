using OnaPlotter.Models;

namespace OnaPlotter.Services.Api;

/// <summary>
/// CRUD for SignalK routes. Returns empty/null on 4xx; throws on network errors.
/// Coordinates are exchanged as Leaflet-ordered [lat, lon] pairs.
/// </summary>
public interface IRouteApi
{
    Task<List<SignalkRoute>> GetAllAsync(CancellationToken ct = default);
    Task<double[][]?> GetCoordinatesAsync(string href, CancellationToken ct = default);
    Task<bool> SaveAsync(string name, double[][] coordsLatLon, CancellationToken ct = default);
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);
}
