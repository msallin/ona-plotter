using System.Text.Json.Serialization;

namespace OnaPlotter.Models;

/// <summary>
/// DTO for a saved route from the SignalK resources API, containing
/// a GeoJSON LineString geometry and optional metadata (name, distance).
/// </summary>
public sealed class SignalkRoute
{
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("distance")]
    public double? Distance { get; set; }

    [JsonPropertyName("feature")]
    public GeoJsonFeature? Feature { get; set; }
}

public sealed class GeoJsonFeature
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("geometry")]
    public GeoJsonGeometry? Geometry { get; set; }

    /// <summary>The properties block written by
    /// <c>GeoJsonBuilder.FeatureBody</c>: <c>{ name, description }</c>
    /// (plus optional hazard flag for regions). Exposed so callers
    /// can recover the description from a server-round-tripped
    /// waypoint - waypoints don't carry a top-level
    /// <c>description</c> field on the wire (only routes and notes do),
    /// so without parsing this block the description silently empties
    /// every time the helm edits the name.</summary>
    [JsonPropertyName("properties")]
    public GeoJsonProperties? Properties { get; set; }
}

public sealed class GeoJsonProperties
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>Region-only marker. Lives here so the same
    /// <see cref="GeoJsonFeature.Properties"/> shape works for the
    /// region path; waypoints / routes ignore it.</summary>
    [JsonPropertyName("isHazard")]
    public bool? IsHazard { get; set; }
}

public sealed class GeoJsonGeometry
{
    /// <summary>
    /// "LineString" for routes, "MultiLineString" for tracks.
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>
    /// For LineString: double[][] where each element is [lon, lat].
    /// For MultiLineString: double[][][] (array of LineStrings).
    /// Raw JSON element because the nesting varies by geometry type.
    /// </summary>
    [JsonPropertyName("coordinates")]
    public System.Text.Json.JsonElement Coordinates { get; set; }
}
