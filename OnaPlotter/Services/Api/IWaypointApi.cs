using OnaPlotter.Models;

namespace OnaPlotter.Services.Api;

/// <summary>CRUD for SignalK waypoints.</summary>
public interface IWaypointApi
{
    Task<List<SignalkWaypoint>> GetAllAsync(CancellationToken ct = default);
    Task<string?> CreateAsync(string name, double lat, double lon, string? description = null, CancellationToken ct = default);
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Rewrites an existing waypoint in place. Keeps the same id +
    /// position; overwrites <paramref name="name"/> and
    /// <paramref name="description"/>. SignalK v2 accepts a PUT with
    /// the full resource body at the waypoint's URL.
    /// </summary>
    Task<bool> UpdateAsync(SignalkWaypoint waypoint, string name, string? description = null, CancellationToken ct = default);
}
