using System.Text.Json.Serialization;

namespace OnaPlotter.Models;

/// <summary>
/// A SignalK region resource: an area on the chart with a title and
/// description. Polygons and multi-polygons share one wire shape
/// (GeoJSON Feature with Polygon/MultiPolygon geometry). Circles are
/// encoded as 32-vertex polygon approximations so consumers don't have
/// to special-case them.
/// </summary>
public sealed class SignalkRegion
{
    [JsonIgnore]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>When true, the region marks a hazardous area; the
    /// <c>HazardousRegionAlarmRule</c> raises a <c>Danger</c> alarm
    /// while own-ship is inside it. Stored both at the wire top level
    /// and in <see cref="GeoJsonFeature.Properties"/> - some Freeboard
    /// builds round-trip the inner copy, others read the outer; we
    /// emit both for compatibility and read whichever the server
    /// happens to send back. Defaults to false so existing decorative
    /// regions don't suddenly start firing alarms.</summary>
    [JsonPropertyName("isHazard")]
    public bool IsHazard { get; set; }

    /// <summary>ISO-8601 UTC timestamp of when this client created the
    /// region. Stored in the resource body so a re-fetch round-trips
    /// the value; the popup renders it as a "Created" line. Null for
    /// regions imported from elsewhere (Freeboard, KIP, GPX import)
    /// or created before this field was introduced; the popup shows
    /// a dash for those rather than a synthetic "now".</summary>
    [JsonPropertyName("createdAt")]
    public DateTime? CreatedAt { get; set; }

    /// <summary>Centre latitude in decimal degrees. Set for regions
    /// created as circles so the popup can render "centre + radius"
    /// instead of a 32-vertex polygon dump. Null for regions created
    /// as freeform polygons or imported from sources that don't
    /// preserve this hint; consumers fall back to the polygon
    /// centroid + vertex count.</summary>
    [JsonPropertyName("centerLat")]
    public double? CenterLat { get; set; }

    /// <summary>Companion to <see cref="CenterLat"/>: centre longitude
    /// for circle-shaped regions. Both fields are written together
    /// or not at all.</summary>
    [JsonPropertyName("centerLon")]
    public double? CenterLon { get; set; }

    /// <summary>Radius in metres for circle-shaped regions. Pairs with
    /// <see cref="CenterLat"/> / <see cref="CenterLon"/> so the popup
    /// can show the helm-friendly "circle, r=250 m" form. Null for
    /// freeform polygons.</summary>
    [JsonPropertyName("radiusMeters")]
    public double? RadiusMeters { get; set; }

    [JsonPropertyName("feature")]
    public GeoJsonFeature? Feature { get; set; }

    /// <summary>
    /// Outer rings extracted from <see cref="Feature"/>, one array per
    /// polygon (a MultiPolygon yields multiple). Each ring is
    /// <c>[[lat, lon], ...]</c> in Leaflet-native order (GeoJSON
    /// <c>[lon, lat]</c> has been swapped). Inner holes are ignored -
    /// SignalK regions in the wild almost never use them and rendering
    /// a single outer boundary is the 99% case.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<double[][]> OuterRings { get; set; } = [];
}
