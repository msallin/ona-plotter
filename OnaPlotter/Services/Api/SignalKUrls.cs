namespace OnaPlotter.Services.Api;

/// <summary>
/// All SignalK server URL paths and ID-based URL builders, in one place.
/// Paths are relative; combine with <see cref="ISignalKBaseUrl"/> for a full URL.
/// Route and waypoint IDs are URL-encoded to handle any characters safely.
/// </summary>
public static class SignalKUrls
{
    public const string StreamPath = "/signalk/v1/stream";
    public const string SelfVesselPath = "/signalk/v1/api/vessels/self";

    public const string ChartsPath = "/signalk/v1/api/resources/charts";
    public const string RoutesPath = "/signalk/v2/api/resources/routes";
    public const string WaypointsPath = "/signalk/v2/api/resources/waypoints";
    public const string TrackPath = "/signalk/v1/api/self/track";

    public const string CoursePath = "/signalk/v2/api/navigation/course";
    public const string CourseDestinationPath = "/signalk/v2/api/navigation/course/destination";

    public const string AutopilotStatePath = "/signalk/v2/api/vessels/self/steering/autopilot/state";
    public const string AutopilotAdjustHeadingPath = "/signalk/v2/api/vessels/self/steering/autopilot/actions/adjustHeading";

    public static string Route(string id) => $"{RoutesPath}/{Uri.EscapeDataString(id)}";
    public static string Waypoint(string id) => $"{WaypointsPath}/{Uri.EscapeDataString(id)}";

    public static string Track(string timespan, string resolution) =>
        $"{TrackPath}?timespan={Uri.EscapeDataString(timespan)}&resolution={Uri.EscapeDataString(resolution)}";

    /// <summary>
    /// Builds the WebSocket stream URL from a base http(s) URL.
    /// Uses ws/wss matching the input scheme. Default subscription is 'none'
    /// (explicit subscriptions are sent over the socket after connect).
    /// </summary>
    public static Uri StreamWs(string baseUrl, string subscribe = "none")
    {
        var b = new Uri(baseUrl);
        string ws = b.Scheme == "https" ? "wss" : "ws";
        return new Uri($"{ws}://{b.Host}:{b.Port}{StreamPath}?subscribe={Uri.EscapeDataString(subscribe)}");
    }

    /// <summary>
    /// Extracts a resource UUID from a SignalK href. Accepts:
    ///   "/resources/routes/{id}", "/signalk/v2/api/resources/routes/{id}", or "{id}".
    /// </summary>
    public static string ExtractRouteId(string href)
    {
        const string marker = "/resources/routes/";
        int idx = href.LastIndexOf(marker, StringComparison.Ordinal);
        return idx >= 0 ? href[(idx + marker.Length)..] : href;
    }
}
