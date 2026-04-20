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
    public double? CourseOverGround { get; private set; }
    public double? Latitude { get; private set; }
    public double? Longitude { get; private set; }
    public double? Heading { get; private set; }
    public double? Depth { get; private set; }
    public double? WindAngleApparent { get; private set; }
    public double? WindSpeedApparent { get; private set; }
    public double? WindAngleTrue { get; private set; }       // TWA (radians, -PI to PI relative to bow)
    public double? WindSpeedTrue { get; private set; }       // TWS (m/s)
    public double? WindDirectionTrue { get; private set; }   // TWD (radians, 0 to 2PI from north)
    public string? LastTimestamp { get; private set; }

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
    public double? CoursePreviousPointLatitude { get; private set; }
    public double? CoursePreviousPointLongitude { get; private set; }
    public bool HasActiveCourse => CourseNextPointLatitude is not null && CourseNextPointLongitude is not null;
    public bool HasPreviousPoint => CoursePreviousPointLatitude is not null && CoursePreviousPointLongitude is not null;

    // Autopilot state
    public string? AutopilotState { get; private set; }        // "standby", "auto", "route", "wind"
    public double? AutopilotTargetHeading { get; private set; } // radians

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

    /// <summary>Sun altitude in radians above the horizon. Positive =
    /// sun above horizon (day); negative = below (night). Typical
    /// thresholds: 0 rad = sunset/sunrise; -0.1 rad ≈ civil twilight;
    /// -0.21 rad ≈ nautical twilight. Null on servers without a solar-
    /// position plugin.</summary>
    public double? SunAltitude { get; private set; }

    /// <summary>Boat draft in metres, sourced from SignalK's
    /// <c>design.draft.current</c> (preferred) or <c>design.draft.maximum</c>
    /// (fallback when the current figure isn't published). Null on
    /// servers that don't advertise design data; in that case the
    /// Settings page's manual override applies instead. Consumers
    /// should pick <c>DraftFromSignalK ?? Settings.BoatDraftMeters</c>
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
                    CourseOverGround = value;
                    break;
                case "navigation.headingTrue":
                    Heading = value;
                    break;
                case "environment.depth.belowTransducer":
                    Depth = value;
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
                    break;
                case "navigation.anchor.currentRadius":
                    AnchorCurrentRadius = value;
                    break;
                case "navigation.courseGreatCircle.nextPoint.distance":
                case "navigation.courseRhumbline.nextPoint.distance":
                    CourseNextPointDistance = value;
                    break;
                case "navigation.courseGreatCircle.nextPoint.bearingTrue":
                case "navigation.courseRhumbline.nextPoint.bearingTrue":
                    CourseNextPointBearing = value;
                    break;
                case "navigation.courseGreatCircle.nextPoint.timeToGo":
                case "navigation.courseRhumbline.nextPoint.timeToGo":
                    CourseNextPointTimeToGo = value;
                    break;
                case "navigation.courseGreatCircle.nextPoint.velocityMadeGood":
                case "navigation.courseRhumbline.nextPoint.velocityMadeGood":
                    CourseNextPointVmg = value;
                    break;
                case "navigation.courseGreatCircle.crossTrackError":
                case "navigation.courseRhumbline.crossTrackError":
                    CrossTrackError = value;
                    break;
                case "steering.autopilot.target.headingTrue":
                    AutopilotTargetHeading = value;
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
                case "environment.sun.altitude":
                    SunAltitude = value;
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
        }
    }

    public void SetTimestamp(string? timestamp)
    {
        lock (_lock)
        {
            LastTimestamp = timestamp;
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
        }
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
                case "navigation.courseGreatCircle.activeRoute.href":
                case "navigation.courseRhumbline.activeRoute.href":
                    ActiveRouteHref = value;
                    break;
                case "navigation.courseGreatCircle.activeRoute.name":
                case "navigation.courseRhumbline.activeRoute.name":
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
