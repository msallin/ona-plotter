// Strongly-typed navigation state populated from SignalK delta updates.
// Values use SI units as per the SignalK spec (radians, m/s, meters, kelvin).
// Display conversion happens in the UI layer.

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
    public bool HasActiveCourse => CourseNextPointLatitude is not null && CourseNextPointLongitude is not null;

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
                case "environment.wind.angleApparent":
                    WindAngleApparent = value;
                    break;
                case "environment.wind.speedApparent":
                    WindSpeedApparent = value;
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
                default:
                    return false;
            }
        }
        return true;
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
