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
    // pointIndex. Used by the "tap a WP on the polyline to skip to it"
    // gesture; lighter than re-PUTting the whole activeRoute because
    // the server only has to flip the leg, not re-resolve the route
    // href.
    public const string CourseActiveRoutePointIndexPath = "/signalk/v2/api/vessels/self/navigation/course/activeRoute/pointIndex";

    /// <summary>Anchor v2.0.0+ standard SK PUT handler endpoints. PUT
    /// <c>navigation.anchor.position</c> with
    /// <c>{value: {latitude, longitude, altitude}}</c> drops; PUT with
    /// <c>{value: null}</c> raises. PUT
    /// <c>navigation.anchor.maxRadius</c> with <c>{value: meters}</c>
    /// arms the alarm circle. v1.x of the plugin doesn't register
    /// these handlers and returns 405 / 404; the helm flow toasts
    /// "v2.0.0+ required" rather than silently retrying.
    /// <para>v1 path prefix per the plugin's v2 docs (the plugin
    /// emits PUT examples on v1, even though resource APIs use v2).</para></summary>
    public const string AnchorPositionPath = "/signalk/v1/api/vessels/self/navigation/anchor/position";
    public const string AnchorMaxRadiusPath = "/signalk/v1/api/vessels/self/navigation/anchor/maxRadius";

    /// <summary>Plugin-specific drop endpoint. Empty JSON body sets
    /// the anchor position from the current GPS without committing
    /// a radius -- the helm's preferred two-step flow (drop now,
    /// pick radius after backing down). The standard SK PUT path
    /// (<see cref="AnchorPositionPath"/>) also works but the plugin
    /// flow matches the plugin's admin UI behaviour exactly +
    /// keeps the lat/lon read centralised in the plugin (no client
    /// guess against a possibly-stale GPS sample).</summary>
    public const string AnchorAlarmDropAnchorPath = "/plugins/anchoralarm/dropAnchor";

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

    /// <summary>SignalK v2 notifications API root. Available on
    /// signalk-server ≥ 2.21.0. Lists every active notification (id +
    /// state + status) and supports POST raise / DELETE clear / per-id
    /// silence + acknowledge actions. We use it for cross-plotter
    /// alarm sync: one helm acknowledges, every plotter sees the ack
    /// via the next delta echo.</summary>
    public const string NotificationsPath = "/signalk/v2/api/notifications";

    /// <summary>POST /{id}/acknowledge -- mark a server notification
    /// acknowledged. Server re-emits the delta with
    /// <c>status.acknowledged = true</c> so every connected plotter
    /// drops its banner in lock-step.</summary>
    public static string NotificationAcknowledge(string id) =>
        $"{NotificationsPath}/{Uri.EscapeDataString(id)}/acknowledge";

    /// <summary>POST /{id}/silence -- hide the audible portion of a
    /// notification while leaving its visual state intact. Useful for
    /// "I see it, stop the klaxon" without clearing the underlying
    /// condition.</summary>
    public static string NotificationSilence(string id) =>
        $"{NotificationsPath}/{Uri.EscapeDataString(id)}/silence";

    /// <summary>DELETE /{id} -- clear a server notification (state
    /// transitions to <c>normal</c> and the entry is GC'd from the
    /// server's in-memory map after 60 s). Used by the "publish
    /// plotter alarms" flow when the underlying client-side rule
    /// stops firing.</summary>
    public static string NotificationById(string id) =>
        $"{NotificationsPath}/{Uri.EscapeDataString(id)}";

    /// <summary>POST /mob -- raise a Man Overboard safety alarm. The
    /// server generates the UUID; clients cannot inject one. Body is
    /// <c>{}</c> or <c>{ "message": "..." }</c>; response carries
    /// <c>{ state, id }</c>.</summary>
    public const string NotificationMobRaise = NotificationsPath + "/mob";

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
    /// Builds the per-radar binary spoke WebSocket URL from a base
    /// http(s) URL. Path matches the SK server's built-in radar stream
    /// proxy (the Mayara SK plugin re-emits spokes through this endpoint).
    /// Mirrors <see cref="StreamWs"/>: ws over http, wss over https.
    /// </summary>
    public static Uri RadarSpokeWs(string baseUrl, string radarId)
    {
        var b = new Uri(baseUrl);
        string ws = b.Scheme == "https" ? "wss" : "ws";
        return new Uri(
            $"{ws}://{b.Host}:{b.Port}/signalk/v2/api/vessels/self/radars/{Uri.EscapeDataString(radarId)}/stream");
    }

    /// <summary>
    /// Validates that a server-supplied spoke WebSocket URL points at
    /// the expected SK origin (scheme is ws/wss, host+port match the
    /// page origin). A hostile or compromised plugin could otherwise
    /// hand the client an attacker-controlled URL and the browser
    /// would dutifully open it -- exfiltration / SSRF-via-browser /
    /// pivoting onto LAN hosts the SK server itself can't reach.
    /// Returns true when the URL is safe to connect to.
    /// </summary>
    public static bool IsSpokeUrlOnSameOrigin(string candidate, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var u)) return false;
        if (u.Scheme != "ws" && u.Scheme != "wss") return false;
        var b = new Uri(baseUrl);
        // Expected scheme transposition: http -> ws, https -> wss.
        string expectedScheme = b.Scheme == "https" ? "wss" : "ws";
        return string.Equals(u.Host, b.Host, StringComparison.OrdinalIgnoreCase)
            && u.Port == b.Port
            && string.Equals(u.Scheme, expectedScheme, StringComparison.Ordinal);
    }

    /// <summary>
    /// Extracts a resource UUID from a SignalK href. Accepts:
    ///   <c>"/resources/routes/{id}"</c>,
    ///   <c>"/signalk/v2/api/resources/routes/{id}"</c>,
    ///   or a bare <c>"{id}"</c>.
    /// <para>
    /// Canonicalises the result so a server emitting a query string,
    /// fragment, or trailing slash on the href doesn't break id-equality
    /// downstream. Without this, helm-tap-to-skip on the active route
    /// silently did nothing if the SK href ever grew a query suffix
    /// (server change or custom plugin) because <c>JumpToRouteWaypoint</c>
    /// compares against the bare-id form.
    /// </para>
    /// </summary>
    public static string ExtractRouteId(string href)
    {
        if (string.IsNullOrEmpty(href)) return href;
        const string marker = "/resources/routes/";
        int idx = href.LastIndexOf(marker, StringComparison.Ordinal);
        var raw = idx >= 0 ? href[(idx + marker.Length)..] : href;
        // Strip fragment first (leftmost on the URL grammar), then query,
        // then trailing slash. Order matters because '?' can appear inside
        // a fragment but '#' before '?' takes precedence.
        int hash = raw.IndexOf('#');
        if (hash >= 0) raw = raw[..hash];
        int q = raw.IndexOf('?');
        if (q >= 0) raw = raw[..q];
        if (raw.EndsWith('/')) raw = raw[..^1];
        return raw;
    }
}
