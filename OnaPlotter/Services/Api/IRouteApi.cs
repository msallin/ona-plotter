using OnaPlotter.Models;

namespace OnaPlotter.Services.Api;

/// <summary>
/// CRUD for SignalK routes. Reads (<see cref="GetAllAsync"/>,
/// <see cref="GetCoordinatesAsync"/>) return empty / null on any
/// failure because the UI should simply not populate; writes return
/// <see cref="ApiResult"/> so callers can surface the server's own
/// error message instead of a generic failure toast.
/// Coordinates are exchanged as Leaflet-ordered [lat, lon] pairs.
/// </summary>
public interface IRouteApi
{
    Task<List<SignalkRoute>> GetAllAsync(CancellationToken ct = default);
    Task<double[][]?> GetCoordinatesAsync(string href, CancellationToken ct = default);
    /// <summary>
    /// POSTs a new route. <c>Value</c> is the server-generated uuid
    /// on success; callers use it to activate the freshly-saved
    /// route without reloading and diffing the full route list.
    /// </summary>
    Task<ApiResult<string>> SaveAsync(string name, double[][] coordsLatLon, CancellationToken ct = default);
    Task<ApiResult> DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Rewrites an existing route in place. Same body shape as
    /// <see cref="SaveAsync"/> but PUTs at the existing id's URL so
    /// the server keeps one route rather than accumulating
    /// "My Passage", "My Passage (edited)", "My Passage (edited 2)"
    /// as the user iterates.
    /// </summary>
    Task<ApiResult> UpdateAsync(string id, string name, double[][] coordsLatLon, CancellationToken ct = default);
}
