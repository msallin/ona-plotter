namespace OnaPlotter.Models;

/// <summary>
/// Thread-safe, strongly-typed navigation state populated from SignalK delta updates.
/// All values use SI units per the SignalK spec (radians, m/s, meters).
/// Display conversion (knots, degrees, etc.) happens in the UI layer.
/// </summary>
public sealed class NavigationData
{
    private readonly Lock _lock = new();

    public double? SpeedOverGround { get; private set; }
    public double? Latitude { get; private set; }
    public double? Longitude { get; private set; }
    public double? Depth { get; private set; }

    // --- Per-field freshness timestamps ---
    // Safety-critical fields carry a UTC update time so HUDs can badge
    // "stale" / "Xs ago" rather than showing a frozen sensor value
    // indefinitely. Covers the audit's "depth sensor dies, HUD reads
    // 3.2m forever" and "GPS loss while anchored" scenarios.
    //
    // Only fields the audit flagged are instrumented; adding new ones
    // is a two-line change (timestamp field + setter assignment). A
    // blanket per-field dictionary would be more uniform but costs
    // allocation per tick; this explicit set is zero-alloc in the hot
    // path.
    public DateTime? DepthUpdatedUtc { get; private set; }
    public DateTime? PositionUpdatedUtc { get; private set; }
    public DateTime? AnchorRadiusUpdatedUtc { get; private set; }

    /// <summary>Clock used for *UpdatedUtc stamps. Defaults to the
    /// system UTC clock; tests inject a deterministic one.</summary>
    private readonly Func<DateTime> _now;

    public NavigationData() : this(null) { }

    /// <summary>Primary ctor; the parameterless overload forwards here
    /// with a null clock (falls back to <see cref="DateTime.UtcNow"/>).</summary>
    public NavigationData(Func<DateTime>? now)
    {
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Returns a freshness category for a timestamped field. Lets the
    /// HUD render "live" / "stale" / "dead" without each card reinventing
    /// the thresholds.
    /// </summary>
    public FieldFreshness FreshnessOf(DateTime? updatedUtc)
    {
        if (updatedUtc is null) return FieldFreshness.Missing;
        var age = _now() - updatedUtc.Value;
        if (age < TimeSpan.FromSeconds(10)) return FieldFreshness.Live;
        if (age < TimeSpan.FromSeconds(30)) return FieldFreshness.Stale;
        return FieldFreshness.Dead;
    }

    // --- Heading / COG: true + magnetic variants ---
    // Servers vary in which heading/COG path they publish. Fluxgate
    // compasses typically emit .magnetic; GPS units with a magnetic-
    // variation fix emit .true. Storing both raw lets the HUD/SailSteer
    // pick whichever the crew wants (see PreferMagneticHeading /
    // PreferMagneticCourse) without losing the other one when it later
    // shows up. Falling back to the other side when the preferred one
    // is null keeps the HUD from going blank on a mis-configured server.

    /// <summary>Raw <c>navigation.headingTrue</c> value (radians, 0..2π).</summary>
    public double? HeadingTrue { get; private set; }

    /// <summary>Raw <c>navigation.headingMagnetic</c> value (radians, 0..2π).</summary>
    public double? HeadingMagnetic { get; private set; }

    /// <summary>Raw <c>navigation.courseOverGroundTrue</c> value (radians, 0..2π).</summary>
    public double? CourseOverGroundTrue { get; private set; }

    /// <summary>Raw <c>navigation.courseOverGroundMagnetic</c> value (radians, 0..2π).</summary>
    public double? CourseOverGroundMagnetic { get; private set; }

    /// <summary>When true, <see cref="Heading"/> resolves to
    /// <see cref="HeadingMagnetic"/> first with <see cref="HeadingTrue"/>
    /// as fallback. Toggled from Settings; the SignalkClient syncs it
    /// on app start and on setting changes.</summary>
    public bool PreferMagneticHeading { get; set; }

    /// <summary>When true, <see cref="CourseOverGround"/> resolves to
    /// <see cref="CourseOverGroundMagnetic"/> first with
    /// <see cref="CourseOverGroundTrue"/> as fallback.</summary>
    public bool PreferMagneticCourse { get; set; }

    /// <summary>Heading in radians, picking true or magnetic according to
    /// <see cref="PreferMagneticHeading"/>. Falls back to the other side
    /// if the preferred one isn't published so HUDs keep a number when
    /// only one of the two paths is live on the server.</summary>
    public double? Heading => PreferMagneticHeading
        ? HeadingMagnetic ?? HeadingTrue
        : HeadingTrue ?? HeadingMagnetic;

    /// <summary>Course-over-ground in radians, picking true or magnetic
    /// per <see cref="PreferMagneticCourse"/>. Same fallback rule as
    /// <see cref="Heading"/>.</summary>
    public double? CourseOverGround => PreferMagneticCourse
        ? CourseOverGroundMagnetic ?? CourseOverGroundTrue
        : CourseOverGroundTrue ?? CourseOverGroundMagnetic;

    public double? WindAngleApparent { get; private set; }
    public double? WindSpeedApparent { get; private set; }
    public double? WindAngleTrue { get; private set; }       // TWA (radians, -PI to PI relative to bow)
    public double? WindSpeedTrue { get; private set; }       // TWS (m/s)
    public double? WindDirectionTrue { get; private set; }   // TWD (radians, 0 to 2PI from north)
    /// <summary>UTC timestamp of the most recent SignalK delta the
    /// client received. Updated regardless of which path(s) the delta
    /// carried, so it answers "when did we last hear from the server?"
    /// rather than "when was this specific value sampled?". Display
    /// callers should convert to local time for the helm.</summary>
    public DateTime? LastReceivedUtc { get; private set; }

    // Anchor alarm data (from signalk-anchoralarm-plugin)
    public double? AnchorLatitude { get; private set; }
    public double? AnchorLongitude { get; private set; }
    public double? AnchorMaxRadius { get; private set; }
    public double? AnchorCurrentRadius { get; private set; }
    public bool AnchorActive => AnchorLatitude is not null && AnchorLongitude is not null;

    // Active course / route info
    public string? ActiveRouteHref { get; private set; }
    public string? ActiveRouteName { get; private set; }
    public double? CourseNextPointLatitude { get; private set; }
    public double? CourseNextPointLongitude { get; private set; }
    public double? CourseNextPointDistance { get; private set; }
    public double? CourseNextPointBearing { get; private set; }
    public double? CourseNextPointTimeToGo { get; private set; }
    public double? CourseNextPointVmg { get; private set; }
    public double? CrossTrackError { get; private set; }

    /// <summary>True when the boat has crossed the perpendicular line
    /// through the active-leg's destination waypoint. Published by the
    /// course-provider plugin at
    /// <c>navigation.course.calcValues.perpendicularPassed</c>. Drives
    /// the auto-advance logic: a true edge (false -> true) triggers an
    /// automatic call to <c>activeRoute/nextPoint</c> when the user has
    /// auto-advance enabled, matching commercial-chartplotter behaviour.
    /// Null when the plugin isn't installed or hasn't computed the
    /// value for the current leg yet.</summary>
    public bool? PerpendicularPassed { get; private set; }

    /// <summary>True when the boat is inside the arrival circle of the
    /// active-leg's destination. Published by the course-provider plugin
    /// at <c>navigation.course.calcValues.arrivalCircleEntered</c>.
    /// Same auto-advance trigger as <see cref="PerpendicularPassed"/>;
    /// either event fires the jump to the next waypoint. Arrival-circle
    /// radius is set per-waypoint by the route author.</summary>
    public bool? ArrivalCircleEntered { get; private set; }

    /// <summary>Distance remaining on the ENTIRE active route (metres).
    /// SignalK v2 path <c>navigation.course.calcValues.route.distance</c>.
    /// Null when there's no active route or when the SK server's
    /// course-provider plugin isn't installed.</summary>
    public double? ActiveRouteDistanceRemaining { get; private set; }

    /// <summary>Time-to-go on the ENTIRE active route (seconds). Analogue of
    /// CourseNextPointTimeToGo but for the route as a whole.</summary>
    public double? ActiveRouteTimeToGo { get; private set; }

    /// <summary>Current leg index (0-based) within the active route. Used
    /// by the HUD to render "WP 3 of 7" progress text.</summary>
    public int? ActiveRoutePointIndex { get; private set; }

    /// <summary>Total waypoints in the active route.</summary>
    public int? ActiveRoutePointTotal { get; private set; }
    public double? CoursePreviousPointLatitude { get; private set; }
    public double? CoursePreviousPointLongitude { get; private set; }
    public bool HasActiveCourse => CourseNextPointLatitude is not null && CourseNextPointLongitude is not null;
    public bool HasPreviousPoint => CoursePreviousPointLatitude is not null && CoursePreviousPointLongitude is not null;

    // Autopilot state
    public string? AutopilotState { get; private set; }        // "standby", "auto", "route", "wind"
    public double? AutopilotTargetHeading { get; private set; } // radians

    /// <summary>Autopilot target apparent wind angle in wind mode
    /// (<c>steering.autopilot.target.windAngleApparent</c>), radians,
    /// -PI..+PI relative to bow. Typical use: AP holds the boat at
    /// this AWA by steering; the extended HDG HUD shows it alongside
    /// the AP state so the crew can see "tracking wind at ~45 deg" at
    /// a glance. Null on servers without a wind-capable AP.</summary>
    public double? AutopilotTargetWindAngle { get; private set; }

    /// <summary>Current rudder angle in radians, negative = port.
    /// Sourced from <c>steering.rudderAngle</c> (most common) with
    /// a fallback to <c>steering.autopilot.rudderAngle</c> which some
    /// AP plugins publish instead. Used by the extended HDG HUD so
    /// the helm can watch how hard the AP is working to hold course.</summary>
    public double? RudderAngle { get; private set; }

    // Tidal current
    public double? CurrentSet { get; private set; }   // Direction current flows TO (radians)
    public double? CurrentDrift { get; private set; }  // Speed of current (m/s)

    // Tide height + next high/low (published by plugins like mxtide /
    // signalk-tides-api). Heights are metres above chart datum; times
    // are UTC. All optional -- silent if no plugin is installed.
    public double? TideHeightNow { get; private set; }
    public double? TideHeightHigh { get; private set; }
    public double? TideHeightLow { get; private set; }
    public DateTime? TideTimeHigh { get; private set; }
    public DateTime? TideTimeLow { get; private set; }
    public string? TideStationName { get; private set; }

    /// <summary>Solar state from SignalK's <c>environment.sun</c> string
    /// path. Typical values from signalk-solar / sun plugins: "day",
    /// "dawn", "dusk", "night". Drives auto-night mode directly -- the
    /// plugin knows about civil / nautical / astronomical twilight so
    /// we don't reimplement those cutoffs here.</summary>
    public string? SunState { get; private set; }

    /// <summary>Boat draft in metres, sourced from SignalK's
    /// <c>design.draft.current</c> (preferred) or <c>design.draft.maximum</c>
    /// (fallback when the current figure isn't published). Null on
    /// servers that don't advertise design data; in that case the
    /// Settings page's manual override applies instead. Consumers
    /// should use <c>DraftFromSignalK</c> directly; stays dormant when null
    /// so a well-configured SK seat wins, but manual still works.</summary>
    public double? DraftFromSignalK { get; private set; }

    /// <summary>
    /// Applies a single SignalK path/value pair to the navigation state.
    /// Returns true if the value was recognized and applied.
    /// </summary>
    public bool Apply(string path, object? rawValue)
    {
        double? value = ConvertToDouble(rawValue);
        if (value is null)
            return false;

        lock (_lock)
        {
            // When adding a new path:
            // 1. Add a property above
            // 2. Add the case here
            // 3. Add a display card in Dashboard.razor
            switch (path)
            {
                case "navigation.speedOverGround":
                    SpeedOverGround = value;
                    break;
                case "navigation.courseOverGroundTrue":
                    CourseOverGroundTrue = value;
                    break;
                case "navigation.courseOverGroundMagnetic":
                    CourseOverGroundMagnetic = value;
                    break;
                case "navigation.headingTrue":
                    HeadingTrue = value;
                    break;
                case "navigation.headingMagnetic":
                    HeadingMagnetic = value;
                    break;
                case "environment.depth.belowTransducer":
                    Depth = value;
                    DepthUpdatedUtc = _now();
                    break;
                case "design.draft.current":
                    // Current is the "as-loaded" draft figure; we prefer
                    // it over maximum when both are published.
                    DraftFromSignalK = value;
                    break;
                case "design.draft.maximum":
                    // Only overwrite if we haven't seen .current yet.
                    // Most servers publish one or the other; a few
                    // publish both and .current is the live reading.
                    DraftFromSignalK ??= value;
                    break;
                case "environment.wind.angleApparent":
                    WindAngleApparent = value;
                    break;
                case "environment.wind.speedApparent":
                    WindSpeedApparent = value;
                    break;
                case "environment.wind.angleTrueWater":
                    WindAngleTrue = value;
                    break;
                case "environment.wind.speedTrue":
                    WindSpeedTrue = value;
                    break;
                case "environment.wind.directionTrue":
                    WindDirectionTrue = value;
                    break;
                case "navigation.anchor.maxRadius":
                    AnchorMaxRadius = value;
                    AnchorRadiusUpdatedUtc = _now();
                    break;
                case "navigation.anchor.currentRadius":
                    AnchorCurrentRadius = value;
                    AnchorRadiusUpdatedUtc = _now();
                    break;
                case "navigation.course.calcValues.distance":
                    CourseNextPointDistance = value;
                    break;
                case "navigation.course.calcValues.bearingTrue":
                    CourseNextPointBearing = value;
                    break;
                case "navigation.course.calcValues.timeToGo":
                    CourseNextPointTimeToGo = value;
                    break;
                case "navigation.course.calcValues.velocityMadeGood":
                    CourseNextPointVmg = value;
                    break;
                case "navigation.course.calcValues.crossTrackError":
                    CrossTrackError = value;
                    break;
                case "steering.autopilot.target.headingTrue":
                    AutopilotTargetHeading = value;
                    break;
                case "steering.autopilot.target.windAngleApparent":
                    AutopilotTargetWindAngle = value;
                    break;
                case "steering.rudderAngle":
                    // Preferred source per SignalK spec; overwrites any
                    // value the autopilot fallback (below) may have set.
                    RudderAngle = value;
                    break;
                case "steering.autopilot.rudderAngle":
                    // Only use as a fallback so a server publishing both
                    // doesn't flap between them. steering.rudderAngle is
                    // the SignalK spec path for the actual rudder position.
                    RudderAngle ??= value;
                    break;
                case "environment.current.setTrue":
                    CurrentSet = value;
                    break;
                case "environment.current.drift":
                    CurrentDrift = value;
                    break;
                case "environment.tide.heightNow":
                    TideHeightNow = value;
                    break;
                case "environment.tide.heightHigh":
                    TideHeightHigh = value;
                    break;
                case "environment.tide.heightLow":
                    TideHeightLow = value;
                    break;
                // "Metres left to cover for the rest of the active
                // route". course-provider-plugin computes this on the
                // SK server + publishes it here; we consume it straight.
                case "navigation.course.calcValues.route.distance":
                    ActiveRouteDistanceRemaining = value;
                    break;
                case "navigation.course.calcValues.route.timeToGo":
                    ActiveRouteTimeToGo = value;
                    break;
                // pointIndex / pointTotal come as numbers too; route them
                // through the number-apply path and cast back to int at
                // the UI boundary so NaN / fractional server quirks don't
                // trip the hot path.
                case "navigation.course.activeRoute.pointIndex":
                    ActiveRoutePointIndex = (int)value;
                    break;
                case "navigation.course.activeRoute.pointTotal":
                    ActiveRoutePointTotal = (int)value;
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Applies a position update (latitude/longitude).
    /// </summary>
    public void ApplyPosition(double latitude, double longitude)
    {
        lock (_lock)
        {
            Latitude = latitude;
            Longitude = longitude;
            PositionUpdatedUtc = _now();
        }
    }

    /// <summary>Marks the moment we just received any SignalK delta.
    /// Used by the dashboard to show "last update" in local time. The
    /// server's own timestamp on the delta is intentionally ignored --
    /// in practice it can be hours out of sync (boats with bad GPS,
    /// clients on a different timezone) and the helm cares about
    /// connection health, not the wire-format timestamp.</summary>
    public void MarkDataReceived()
    {
        lock (_lock)
        {
            LastReceivedUtc = _now();
        }
    }

    public void ApplyAnchorPosition(double latitude, double longitude)
    {
        lock (_lock)
        {
            AnchorLatitude = latitude;
            AnchorLongitude = longitude;
        }
    }

    public void ClearAnchor()
    {
        lock (_lock)
        {
            AnchorLatitude = null;
            AnchorLongitude = null;
            AnchorMaxRadius = null;
            AnchorCurrentRadius = null;
        }
    }

    public void ApplyCourseNextPointPosition(double latitude, double longitude)
    {
        lock (_lock)
        {
            CourseNextPointLatitude = latitude;
            CourseNextPointLongitude = longitude;
        }
    }

    public void ApplyCoursePreviousPointPosition(double latitude, double longitude)
    {
        lock (_lock)
        {
            CoursePreviousPointLatitude = latitude;
            CoursePreviousPointLongitude = longitude;
        }
    }

    /// <summary>
    /// Resets all course/route fields when a route is deactivated.
    /// </summary>
    public void ClearCourse()
    {
        lock (_lock)
        {
            ActiveRouteHref = null;
            ActiveRouteName = null;
            CourseNextPointLatitude = null;
            CourseNextPointLongitude = null;
            CourseNextPointDistance = null;
            CourseNextPointBearing = null;
            CourseNextPointTimeToGo = null;
            CourseNextPointVmg = null;
            CrossTrackError = null;
            CoursePreviousPointLatitude = null;
            CoursePreviousPointLongitude = null;
            ActiveRouteDistanceRemaining = null;
            ActiveRouteTimeToGo = null;
            ActiveRoutePointIndex = null;
            ActiveRoutePointTotal = null;
            PerpendicularPassed = null;
            ArrivalCircleEntered = null;
        }
    }

    /// <summary>
    /// Applies a boolean-valued SignalK path. Returns true if recognised.
    /// Used for <c>perpendicularPassed</c> / <c>arrivalCircleEntered</c>
    /// from the course-provider plugin, where the wire type is a plain
    /// JSON true/false rather than a numeric 0/1.
    /// </summary>
    public bool ApplyBool(string path, bool value)
    {
        lock (_lock)
        {
            switch (path)
            {
                // Course Providers plugin emits these as notifications
                // (notifications.navigation.course.<flag>); the old
                // spec doc also lists the bare navigation.course.<flag>
                // path and some plugin builds put them under calcValues.
                // Accept all three so a plugin update doesn't silently
                // break auto-advance.
                case "notifications.navigation.course.perpendicularPassed":
                case "navigation.course.perpendicularPassed":
                case "navigation.course.calcValues.perpendicularPassed":
                    PerpendicularPassed = value;
                    break;
                case "notifications.navigation.course.arrivalCircleEntered":
                case "navigation.course.arrivalCircleEntered":
                case "navigation.course.calcValues.arrivalCircleEntered":
                    ArrivalCircleEntered = value;
                    break;
                default:
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Applies a string-valued SignalK path (route href, route name).
    /// Returns true if recognized.
    /// </summary>
    public bool ApplyString(string path, string? value)
    {
        lock (_lock)
        {
            switch (path)
            {
                case "navigation.course.activeRoute.href":
                    ActiveRouteHref = value;
                    break;
                case "navigation.course.activeRoute.name":
                    ActiveRouteName = value;
                    break;
                case "steering.autopilot.state":
                    AutopilotState = value;
                    break;
                case "environment.tide.timeHigh":
                    TideTimeHigh = ParseUtc(value);
                    break;
                case "environment.tide.timeLow":
                    TideTimeLow = ParseUtc(value);
                    break;
                case "environment.tide.stationName":
                    TideStationName = value;
                    break;
                case "environment.sun":
                    // Published by some solar plugins as a plain string
                    // ("day" / "dawn" / "dusk" / "night"). Normalised to
                    // lower-case so downstream comparisons don't trip on
                    // case drift between plugins.
                    SunState = string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
                    break;
                default:
                    return false;
            }
        }
        return true;
    }

    // Tide-plugin timestamps come as ISO 8601 strings. Accept either
    // "...Z" or an unqualified string (which we then pin to UTC) so
    // downstream code comparing against UtcNow doesn't get bitten by
    // Kind=Unspecified.
    private static DateTime? ParseUtc(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var dt))
            return null;
        return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
    }

    private static double? ConvertToDouble(object? raw)
    {
        if (raw is null) return null;
        if (raw is double d) return d;
        if (raw is int i) return i;
        if (raw is long l) return l;
        if (raw is float f) return f;
        if (raw is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Number)
            return je.GetDouble();
        return null;
    }
}
