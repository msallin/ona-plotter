using System.Text.Json.Serialization;

namespace OnaPlotter.Models;

/// <summary>
/// A SignalK region resource: an area on the chart with a title and
/// description. Polygons and multi-polygons share one wire shape
/// (GeoJSON Feature with Polygon/MultiPolygon geometry). Circles are
/// encoded as 32-vertex polygon approximations so consumers don't have
/// to special-case them -- Freeboard-SK does the same.
/// </summary>
public sealed class SignalkRegion
{
    [JsonIgnore]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("feature")]
    public GeoJsonFeature? Feature { get; set; }

    /// <summary>
    /// Outer rings extracted from <see cref="Feature"/>, one array per
    /// polygon (a MultiPolygon yields multiple). Each ring is
    /// <c>[[lat, lon], ...]</c> in Leaflet-native order (GeoJSON
    /// <c>[lon, lat]</c> has been swapped). Inner holes are ignored --
    /// SignalK regions in the wild almost never use them and rendering
    /// a single outer boundary is the 99% case.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<double[][]> OuterRings { get; set; } = [];
}
