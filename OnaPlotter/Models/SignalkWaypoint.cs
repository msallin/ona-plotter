// DTO for a waypoint resource from SignalK.
// GET /signalk/v2/api/resources/waypoints returns an object keyed by UUID.
// Each waypoint has a GeoJSON Feature with Point geometry.

using System.Text.Json.Serialization;

namespace OnaPlotter.Models;

public sealed class SignalkWaypoint
{
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("feature")]
    public GeoJsonFeature? Feature { get; set; }

    // Convenience properties extracted from GeoJSON Point coordinates.
    [JsonIgnore]
    public double? Latitude { get; set; }

    [JsonIgnore]
    public double? Longitude { get; set; }
}
