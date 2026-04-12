// DTO for a route resource from SignalK.
// GET /signalk/v2/api/resources/routes returns an object keyed by UUID.
// Each route has a GeoJSON Feature with LineString geometry.

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
