using OnaPlotter.Models;

namespace OnaPlotter.Services.Api;

/// <summary>CRUD for SignalK regions (polygonal areas on the chart).
/// Supports two creation modes: a radius-based circle (approximated as
/// a 32-vertex polygon on the wire) and a freeform polygon from a list
/// of [lat, lon] vertices.</summary>
public interface IRegionApi
{
    Task<List<SignalkRegion>> GetAllAsync(CancellationToken ct = default);

    Task<ApiResult<string>> CreateCircleAsync(string name, string description,
        double lat, double lon, double radiusMeters, CancellationToken ct = default);

    /// <summary>
    /// Creates a freeform polygon region from a list of <c>[lat, lon]</c>
    /// vertices (Leaflet order). The first vertex is automatically
    /// repeated at the end to close the ring, per GeoJSON. Requires at
    /// least 3 distinct vertices; fails otherwise with an "invalid
    /// input" error so the caller can toast the reason.
    /// </summary>
    Task<ApiResult<string>> CreatePolygonAsync(string name, string description,
        double[][] vertices, CancellationToken ct = default);

    Task<ApiResult> DeleteAsync(string id, CancellationToken ct = default);
}
