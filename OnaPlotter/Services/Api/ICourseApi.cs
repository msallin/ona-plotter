namespace OnaPlotter.Services.Api;

/// <summary>
/// Controls the active navigation course: set destination to a waypoint, clear course.
/// </summary>
public interface ICourseApi
{
    Task<bool> SetDestinationAsync(string waypointId, CancellationToken ct = default);
    Task<bool> SetDestinationPositionAsync(double latitude, double longitude, CancellationToken ct = default);

    /// <summary>Activates a saved route so SignalK will drive the
    /// active-course delta stream through it leg-by-leg. Sets the active
    /// waypoint to <paramref name="pointIndex"/> (0-based). Matches the
    /// shape Freeboard-SK sends so both apps can start the same route.</summary>
    Task<bool> SetActiveRouteAsync(string routeId, int pointIndex = 0,
        bool reverse = false, CancellationToken ct = default);

    Task<bool> ClearAsync(CancellationToken ct = default);
}
