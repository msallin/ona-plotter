using OnaPlotter.Models;

namespace OnaPlotter.Services.Api;

/// <summary>CRUD for SignalK waypoints.</summary>
public interface IWaypointApi
{
    Task<List<SignalkWaypoint>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Creates a waypoint; <see cref="ApiResult{T}.Value"/>
    /// is the server-assigned id on success.</summary>
    Task<ApiResult<string>> CreateAsync(string name, double lat, double lon, string? description = null, CancellationToken ct = default);

    /// <summary>Create overload with MOB metadata. Used by
    /// <c>MobService.RaiseAsync</c> to pin a Man-Overboard
    /// waypoint with <c>isMob: true</c>, <c>isActive: true</c>, and
    /// the SignalK notification id (<c>mobAlarmId</c>) so any
    /// plotter observing the corresponding <c>notifications.mob.X</c>
    /// delta can correlate it with this waypoint. Non-MOB callers
    /// use the simpler 4-arg overload above.</summary>
    Task<ApiResult<string>> CreateAsync(string name, double lat, double lon,
        string? description, bool? isMob, bool? isActive, string? mobAlarmId,
        CancellationToken ct = default);

    Task<ApiResult> DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Rewrites an existing waypoint in place. Keeps the same id +
    /// position; overwrites <paramref name="name"/> and
    /// <paramref name="description"/>. SignalK v2 accepts a PUT with
    /// the full resource body at the waypoint's URL.
    /// </summary>
    Task<ApiResult> UpdateAsync(SignalkWaypoint waypoint, string name, string? description = null, CancellationToken ct = default);

    /// <summary>Update overload that also writes MOB metadata. Used
    /// by <c>MobService.ClearAsync</c> to flip <c>isActive: false</c>
    /// without deleting the waypoint - the helm gets a persistent
    /// MOB history that survives reload + reconnect.</summary>
    Task<ApiResult> UpdateAsync(SignalkWaypoint waypoint, string name, string? description,
        bool? isMob, bool? isActive, string? mobAlarmId, CancellationToken ct = default);
}
