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

    /// <summary>
    /// Rewrites an existing route in place. Same body shape as
    /// <see cref="SaveAsync"/> but PUTs at the existing id's URL so
    /// the server keeps one route rather than accumulating
    /// "My Passage", "My Passage (edited)", "My Passage (edited 2)"
    /// as the user iterates.
    /// </summary>
    Task<bool> UpdateAsync(string id, string name, double[][] coordsLatLon, CancellationToken ct = default);
}
