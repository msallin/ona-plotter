namespace OnaPlotter.Services.Api;

/// <summary>
/// Controls the active navigation course: set destination to a waypoint,
/// activate a saved route, clear course, advance to next waypoint.
///
/// Every method is a thin PUT/DELETE over the SignalK v2
/// <c>/navigation/course</c> endpoint family -- OnaPlotter does NOT
/// maintain its own route-execution state. The server's course engine
/// owns <c>activeRoute.pointIndex</c>, DTG, BTW, TTG, XTE; the client
/// reads them back as deltas on <c>navigation.course*.nextPoint.*</c>.
/// Freeboard-SK talks the same API against the same server, so both
/// apps see a consistent course at all times.
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

    /// <summary>Advances the active route to the next waypoint. PUT with
    /// an empty body; the server increments its own pointIndex. Used by
    /// the Next-WP button on the APPROACH alarm banner so the helm can
    /// confirm "arrived, on to the next leg" in one tap.</summary>
    Task<bool> AdvanceActiveRouteAsync(CancellationToken ct = default);

    Task<bool> ClearAsync(CancellationToken ct = default);
}
