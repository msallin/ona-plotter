namespace OnaPlotter.Services.Api;

using OnaPlotter.Services.Settings;

public sealed class CourseApi : ICourseApi
{
    private readonly HttpClient _http;
    private readonly ISignalKBaseUrl _baseUrl;
    private readonly INavPreferences _navPrefs;

    public CourseApi(HttpClient http, ISignalKBaseUrl baseUrl, INavPreferences navPrefs)
    {
        _http = http;
        _baseUrl = baseUrl;
        _navPrefs = navPrefs;
    }

    public Task<ApiResult> SetDestinationAsync(string waypointId, CancellationToken ct = default)
    {
        // arrivalCircle is per the SK v2 Course API spec - the server
        // adopts our value as the effective navigation.course.arrivalCircle
        // so HUD ring + APPROACH alarm + auto-advance use it. Sourced
        // from helm settings (INavPreferences); the local read-side
        // (NavigationData.CourseArrivalCircleMeters) keeps mirroring
        // the SK delta so a peer plotter overwriting it still propagates.
        var body = new
        {
            href = $"/resources/waypoints/{Uri.EscapeDataString(waypointId)}",
            arrivalCircle = _navPrefs.ArrivalCircleMeters,
        };
        return ResourceHttp.PutAsync(_http, _baseUrl.Combine(SignalKUrls.CourseDestinationPath), body, ct);
    }

    /// <summary>Sets a direct lat/lon destination - used for the Stop
    /// Navigation undo path, where we want to restore to an arbitrary point
    /// without having to identify the original waypoint or route.</summary>
    public Task<ApiResult> SetDestinationPositionAsync(double latitude, double longitude, CancellationToken ct = default)
    {
        var body = new
        {
            position = new { latitude, longitude },
            arrivalCircle = _navPrefs.ArrivalCircleMeters,
        };
        return ResourceHttp.PutAsync(_http, _baseUrl.Combine(SignalKUrls.CourseDestinationPath), body, ct);
    }

    public Task<ApiResult> SetActiveRouteAsync(string routeId, int pointIndex = 0,
        bool reverse = false, CancellationToken ct = default)
    {
        // SignalK v2 active-route body shape: href is the v1 relative
        // resource path (NOT the v2 full API path), pointIndex is
        // zero-based, reverse flips the leg direction. Servers that
        // don't honour pointIndex will default to the first leg,
        // which is still the common case. arrivalCircle plumbs the
        // helm-configured radius into the leg-by-leg APPROACH check.
        var body = new
        {
            href = $"/resources/routes/{Uri.EscapeDataString(routeId)}",
            pointIndex,
            reverse,
            arrivalCircle = _navPrefs.ArrivalCircleMeters,
        };
        return ResourceHttp.PutAsync(_http, _baseUrl.Combine(SignalKUrls.CourseActiveRoutePath), body, ct);
    }

    public Task<ApiResult> AdvanceActiveRouteAsync(CancellationToken ct = default)
    {
        // SignalK v2 Course API: PUT /activeRoute/nextPoint with body
        // {"value": n} where n is the number of positions to advance.
        // +1 = next waypoint, -1 = previous. The older empty-body form
        // worked on some server builds but is non-spec; the explicit
        // value is what the docs mandate.
        var url = _baseUrl.Combine(SignalKUrls.CourseActiveRouteNextPointPath);
        return ResourceHttp.PutAsync(_http, url, body: new { value = 1 }, ct);
    }

    public Task<ApiResult> SetPointIndexAsync(int pointIndex, CancellationToken ct = default)
    {
        // SignalK v2 Course API: PUT /activeRoute/pointIndex with body
        // {"value": N} where N is the 0-based absolute leg index.
        // Server clamps out-of-range and recomputes the rest of the
        // course state on its end.
        var url = _baseUrl.Combine(SignalKUrls.CourseActiveRoutePointIndexPath);
        return ResourceHttp.PutAsync(_http, url, body: new { value = pointIndex }, ct);
    }

    public Task<ApiResult> ClearAsync(CancellationToken ct = default) =>
        ResourceHttp.DeleteAsync(_http, _baseUrl.Combine(SignalKUrls.CoursePath), ct);
}
