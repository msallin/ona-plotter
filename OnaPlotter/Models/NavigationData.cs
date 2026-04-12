// Strongly-typed navigation state populated from SignalK delta updates.
// Values use SI units as per the SignalK spec (radians, m/s, meters, kelvin).
// Display conversion happens in the UI layer.

namespace OnaPlotter.Models;

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
                case "navigation.position":
                    // Position arrives as { "latitude": ..., "longitude": ... }
                    // Handled separately in ApplyPosition.
                    return false;
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
