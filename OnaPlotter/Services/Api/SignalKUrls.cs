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
    public const string NotesPath = "/signalk/v2/api/resources/notes";
    public const string RegionsPath = "/signalk/v2/api/resources/regions";
    /// <summary>v2 History API values endpoint (signalk-parquet,
    /// signalk-to-influxdb2). Answers path-value queries over an
    /// arbitrary time window with optional aggregation; we use it
    /// as the primary source for the History page because it's
    /// resolution-tunable (the /resources/tracks endpoint returns
    /// whatever cadence the recorder used) and gives us a clean
    /// [timestamp, value] stream to drive the playback slider.
    ///
    /// Canonical shape:
    ///   GET /signalk/v2/api/history/values
    ///     ?paths=navigation.position
    ///     &amp;duration=PT1H              (ISO 8601)
    ///     &amp;resolution=30s
    ///
    /// Response:
    ///   {
    ///     "context": "vessels.urn:mrn:...",
    ///     "range": { "from": "...", "to": "..." },
    ///     "values": [{ "path": "navigation.position", "method": "first" }],
    ///     "data": [[ "2026-04-23T14:43:48Z", [-76.82, 24.59] ], ...]
    ///   }
    ///
    /// Position values are [lon, lat] (GeoJSON order); caller flips
    /// to Leaflet's [lat, lon].
    /// </summary>
    public const string HistoryValuesPath = "/signalk/v2/api/history/values";

    // Course paths MUST include /vessels/self/ per the SignalK v2
    // Course API spec. Earlier these omitted the prefix and the
    // server returned 404 on every call -- Set Destination, Set
    // Active Route, Advance Next Point, Clear. Auto-advance
    // looked broken because even when the notification edge fired,
    // CourseApi.AdvanceActiveRouteAsync hit a 404 and the server
    // never actually flipped legs. Same prefix the Autopilot
    // paths already use (AutopilotStatePath, etc.).
    public const string CoursePath = "/signalk/v2/api/vessels/self/navigation/course";
    public const string CourseDestinationPath = "/signalk/v2/api/vessels/self/navigation/course/destination";
    public const string CourseActiveRoutePath = "/signalk/v2/api/vessels/self/navigation/course/activeRoute";
    public const string CourseActiveRouteNextPointPath = "/signalk/v2/api/vessels/self/navigation/course/activeRoute/nextPoint";
    // Absolute jump to a leg index. Body is {value: N} with N the 0-based
    // pointIndex. Mirrors Freeboard-SK's "tap a WP on the polyline to
    // skip to it" gesture; lighter than re-PUTting the whole activeRoute
    // because the server only has to flip the leg, not re-resolve the
    // route href.
    public const string CourseActiveRoutePointIndexPath = "/signalk/v2/api/vessels/self/navigation/course/activeRoute/pointIndex";

    public const string AutopilotStatePath = "/signalk/v2/api/vessels/self/steering/autopilot/state";
    public const string AutopilotAdjustHeadingPath = "/signalk/v2/api/vessels/self/steering/autopilot/actions/adjustHeading";

    /// <summary>Root of the v3.1 Radar API. Discovery + per-device
    /// capabilities / controls / targets live as children. Spoke data
    /// is on a separate WebSocket per radar, advertised in each
    /// radar's <c>spokeDataUrl</c> response field.</summary>
    public const string RadarsPath = "/signalk/v2/api/vessels/self/radars";

    public static string Radar(string id) => $"{RadarsPath}/{Uri.EscapeDataString(id)}";
    public static string RadarCapabilities(string id) => $"{Radar(id)}/capabilities";
    public static string RadarControls(string id) => $"{Radar(id)}/controls";
    public static string RadarControl(string id, string controlId) =>
        $"{RadarControls(id)}/{Uri.EscapeDataString(controlId)}";
    public static string RadarTargets(string id) => $"{Radar(id)}/targets";

    /// <summary>REST API exposed by sbender9/signalk-buddylist-plugin.
    /// A 200 means the plugin is installed and running; 404 means it isn't.</summary>
    public const string BuddiesPath = "/signalk/v2/api/resources/buddies";

    /// <summary>Open-Meteo's hourly global forecast API. Free, no key, no
    /// CORS. Returns JSON with parallel arrays of hourly samples keyed by
    /// the variables we request. Used by the weather router.</summary>
    public const string OpenMeteoBase = "https://api.open-meteo.com/v1/forecast";

    public static string Buddy(string urn) => $"{BuddiesPath}/{Uri.EscapeDataString(urn)}";

    public static string Route(string id) => $"{RoutesPath}/{Uri.EscapeDataString(id)}";
    public static string Waypoint(string id) => $"{WaypointsPath}/{Uri.EscapeDataString(id)}";
    public static string Note(string id) => $"{NotesPath}/{Uri.EscapeDataString(id)}";
    public static string Region(string id) => $"{RegionsPath}/{Uri.EscapeDataString(id)}";

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
