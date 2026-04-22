using OnaPlotter.Models;

namespace OnaPlotter.Services.Api;

/// <summary>CRUD for SignalK waypoints.</summary>
public interface IWaypointApi
{
    Task<List<SignalkWaypoint>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Creates a waypoint; <see cref="ApiResult{T}.Value"/>
    /// is the server-assigned id on success.</summary>
    Task<ApiResult<string>> CreateAsync(string name, double lat, double lon, string? description = null, CancellationToken ct = default);

    Task<ApiResult> DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Rewrites an existing waypoint in place. Keeps the same id +
    /// position; overwrites <paramref name="name"/> and
    /// <paramref name="description"/>. SignalK v2 accepts a PUT with
    /// the full resource body at the waypoint's URL.
    /// </summary>
    Task<ApiResult> UpdateAsync(SignalkWaypoint waypoint, string name, string? description = null, CancellationToken ct = default);
}
