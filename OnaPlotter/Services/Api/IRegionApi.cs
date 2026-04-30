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
        double lat, double lon, double radiusMeters,
        bool isHazard = false, CancellationToken ct = default);

    /// <summary>
    /// Creates a freeform polygon region from a list of <c>[lat, lon]</c>
    /// vertices (Leaflet order). The first vertex is automatically
    /// repeated at the end to close the ring, per GeoJSON. Requires at
    /// least 3 distinct vertices; fails otherwise with an "invalid
    /// input" error so the caller can toast the reason.
    /// <para>The <paramref name="isHazard"/> flag turns the region into
    /// active safety geometry: when own-ship is inside a hazardous
    /// region, <c>HazardousRegionAlarmRule</c> raises a Danger alarm
    /// with the region's name. Defaults to false (decorative).</para>
    /// </summary>
    Task<ApiResult<string>> CreatePolygonAsync(string name, string description,
        double[][] vertices,
        bool isHazard = false, CancellationToken ct = default);

    /// <summary>Rewrites an existing polygon region (name + description
    /// + vertices + hazard flag) in place via PUT. Used by the
    /// Layers-panel Edit button; parallels RouteApi.UpdateAsync.</summary>
    Task<ApiResult> UpdatePolygonAsync(string id, string name, string description,
        double[][] vertices,
        bool isHazard = false, CancellationToken ct = default);

    Task<ApiResult> DeleteAsync(string id, CancellationToken ct = default);
}
