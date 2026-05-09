using System.Text.Json.Serialization;

namespace OnaPlotter.Models;

public sealed class SignalkWaypoint
{
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("feature")]
    public GeoJsonFeature? Feature { get; set; }

    /// <summary>Helm-authored note text. Waypoints don't carry a
    /// top-level <c>description</c> on the wire (only routes / regions
    /// / notes do); the value lives in
    /// <see cref="GeoJsonFeature.Properties"/>. Populated by
    /// <c>WaypointApi.GetAllAsync</c> from <c>Feature.Properties.Description</c>
    /// so the popup-Edit flow can pre-fill the description field with
    /// the previously-saved value rather than emptying it on rename.
    /// Marked JsonIgnore so the C# field doesn't accidentally
    /// re-emit a top-level <c>description</c> on Create/Update - the
    /// wire side stays in <c>feature.properties.description</c>.</summary>
    [JsonIgnore]
    public string? Description { get; set; }

    /// <summary>UTC instant the waypoint was first PUT to the server.
    /// Same custom-field approach as <see cref="SignalkNote.CreatedAt"/>:
    /// the SK Waypoint schema doesn't include this; we ride along on
    /// the resource body and rely on resources-fs preserving the
    /// field on round-trip. Waypoints created by other clients won't
    /// carry it - the popup renders a dash for those rather than a
    /// fake "now".</summary>
    [JsonPropertyName("createdAt")]
    public DateTime? CreatedAt { get; set; }

    // Convenience properties extracted from GeoJSON Point coordinates.
    [JsonIgnore]
    public double? Latitude { get; set; }

    [JsonIgnore]
    public double? Longitude { get; set; }
}
