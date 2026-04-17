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
