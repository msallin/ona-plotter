using System.Text.Json.Serialization;

namespace OnaPlotter.Models;

public sealed class SignalkWaypoint
{
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("feature")]
    public GeoJsonFeature? Feature { get; set; }

    /// <summary>UTC instant the waypoint was first PUT to the server.
    /// Same custom-field approach as <see cref="SignalkNote.CreatedAt"/>:
    /// the SK Waypoint schema doesn't include this; we ride along on
    /// the resource body and rely on resources-fs preserving the
    /// field on round-trip. Waypoints created by other clients
    /// (Freeboard, KIP) won't carry it -- the popup renders a dash
    /// for those rather than a fake "now".</summary>
    [JsonPropertyName("createdAt")]
    public DateTime? CreatedAt { get; set; }

    // Convenience properties extracted from GeoJSON Point coordinates.
    [JsonIgnore]
    public double? Latitude { get; set; }

    [JsonIgnore]
    public double? Longitude { get; set; }
}
