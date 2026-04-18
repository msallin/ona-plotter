namespace OnaPlotter.Services.Api;

/// <summary>
/// Controls the active navigation course: set destination to a waypoint, clear course.
/// </summary>
public interface ICourseApi
{
    Task<bool> SetDestinationAsync(string waypointId, CancellationToken ct = default);
    Task<bool> SetDestinationPositionAsync(double latitude, double longitude, CancellationToken ct = default);
    Task<bool> ClearAsync(CancellationToken ct = default);
}
