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

    /// <summary>Waypoint-only marker. <c>true</c> stamps this
    /// waypoint as a Man-Overboard pin: the chart renders it with
    /// the pulsing red icon + the "MOB" overlay instead of a
    /// regular waypoint dot. The flag stays set forever after the
    /// MOB is raised so the helm has a persistent history. The
    /// audible alarm + banner pipeline is independent (driven by
    /// the SignalK <c>notifications.mob.*</c> path); see
    /// <see cref="MobAlarmId"/> for the correlation.</summary>
    [JsonPropertyName("isMob")]
    public bool? IsMob { get; set; }

    /// <summary>True while the MOB alarm is still firing for this
    /// waypoint. Cleared (set to <c>false</c>) when the helm
    /// dismisses the MOB; the waypoint stays on the chart but
    /// stops pulsing. Only meaningful when <see cref="IsMob"/>
    /// is true. Wire tag stays <c>"isActive"</c> for compatibility
    /// with already-saved resources; the C# name is MOB-prefixed so
    /// a future "is route active" / "is region active" boolean
    /// doesn't collide with this MOB-only semantic.</summary>
    [JsonPropertyName("isActive")]
    public bool? IsMobActive { get; set; }

    /// <summary>SignalK notification id (the path tail of
    /// <c>notifications.mob.&lt;id&gt;</c>) that this waypoint
    /// corresponds to. Lets any plotter observing a
    /// <c>notifications.mob.X</c> delta find the matching waypoint
    /// (and vice versa) so the alarm + chart marker stay
    /// linked across plotters and across reload. Only set when
    /// <see cref="IsMob"/> is true.</summary>
    [JsonPropertyName("mobAlarmId")]
    public string? MobAlarmId { get; set; }
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
