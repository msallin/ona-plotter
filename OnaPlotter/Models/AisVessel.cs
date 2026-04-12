// AIS target vessel state, populated from SignalK delta updates for non-self contexts.

using System.Text.Json;

namespace OnaPlotter.Models;

/// <summary>
/// Mutable state for a single AIS target vessel, populated from SignalK delta
/// updates for non-self vessel contexts. Tracks position, identity, and motion.
/// </summary>
public sealed class AisVessel
{
    public string Context { get; }
    public string? Name { get; set; }
    public string? Mmsi { get; set; }
    public string? Callsign { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public double? Heading { get; set; }
    public double? CourseOverGround { get; set; }
    public double? SpeedOverGround { get; set; }
    public string? ShipType { get; set; }
    public DateTime LastSeen { get; set; }

    public AisVessel(string context)
    {
        Context = context;
        LastSeen = DateTime.UtcNow;
    }

    /// <summary>
    /// Applies a SignalK path/value pair. Returns true if the value was recognized.
    /// </summary>
    public bool Apply(string path, object? rawValue)
    {
        LastSeen = DateTime.UtcNow;

        switch (path)
        {
            case "navigation.position":
                if (rawValue is JsonElement posEl && posEl.ValueKind == JsonValueKind.Object
                    && posEl.TryGetProperty("latitude", out var lat)
                    && posEl.TryGetProperty("longitude", out var lon)
                    && lat.ValueKind == JsonValueKind.Number
                    && lon.ValueKind == JsonValueKind.Number)
                {
                    Latitude = lat.GetDouble();
                    Longitude = lon.GetDouble();
                    return true;
                }
                return false;

            case "navigation.headingTrue":
                Heading = ToDouble(rawValue);
                return Heading is not null;

            case "navigation.courseOverGroundTrue":
                CourseOverGround = ToDouble(rawValue);
                return CourseOverGround is not null;

            case "navigation.speedOverGround":
                SpeedOverGround = ToDouble(rawValue);
                return SpeedOverGround is not null;

            case "name":
                Name = rawValue is JsonElement nameEl && nameEl.ValueKind == JsonValueKind.String
                    ? nameEl.GetString() : rawValue?.ToString();
                return true;

            case "mmsi":
                Mmsi = rawValue is JsonElement mmsiEl && mmsiEl.ValueKind == JsonValueKind.String
                    ? mmsiEl.GetString() : rawValue?.ToString();
                return true;

            case "communication.callsignVhf":
                Callsign = rawValue is JsonElement csEl && csEl.ValueKind == JsonValueKind.String
                    ? csEl.GetString() : rawValue?.ToString();
                return true;

            case "design.aisShipType":
                if (rawValue is JsonElement typeEl)
                    ShipType = typeEl.TryGetProperty("name", out var n) ? n.GetString() : typeEl.ToString();
                else
                    ShipType = rawValue?.ToString();
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Extracts MMSI from a context string like "vessels.urn:mrn:imo:mmsi:211234567".
    /// </summary>
    public static string? ExtractMmsi(string context)
    {
        // Example: "vessels.urn:mrn:imo:mmsi:211234567"
        const string prefix = "mmsi:";
        int idx = context.IndexOf(prefix, StringComparison.Ordinal);
        if (idx < 0) return null;
        return context[(idx + prefix.Length)..];
    }

    private static double? ToDouble(object? raw)
    {
        if (raw is null) return null;
        if (raw is double d) return d;
        if (raw is JsonElement je && je.ValueKind == JsonValueKind.Number) return je.GetDouble();
        return null;
    }
}
