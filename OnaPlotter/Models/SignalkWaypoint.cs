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

    /// <summary>Convenience projection of <see cref="GeoJsonProperties.IsMob"/>.
    /// Populated by <c>WaypointApi.GetAllAsync</c> and
    /// <c>ResourceStore.HandleWaypointDelta</c> when the underlying
    /// <c>feature.properties.isMob</c> field is true. The chart and
    /// the layers panel use this flag to render the MOB icon +
    /// pulse instead of a regular dot.</summary>
    [JsonIgnore]
    public bool IsMob { get; set; }

    /// <summary>True while the MOB alarm is still active for this
    /// waypoint. Source: <see cref="GeoJsonProperties.IsMobActive"/>.
    /// Drives the chart marker's pulsing animation; helm-dismiss of
    /// the MOB flips this to false but the waypoint stays so the
    /// helm has a persistent history of past MOBs.</summary>
    [JsonIgnore]
    public bool IsMobActive { get; set; }

    /// <summary>SignalK <c>notifications.mob.&lt;id&gt;</c> id this
    /// waypoint correlates with. Source:
    /// <see cref="GeoJsonProperties.MobAlarmId"/>. Used by
    /// <c>MobService.ClearAsync</c> to find the right waypoint when
    /// the helm dismisses an alarm.</summary>
    [JsonIgnore]
    public string? MobAlarmId { get; set; }
}

/// <summary>Helpers for the post-deserialise field lift from
/// <see cref="GeoJsonFeature.Properties"/> onto the flat
/// <see cref="SignalkWaypoint"/> projections. Centralised here so
/// the REST path (<c>WaypointApi.GetAllAsync</c>) and the WS path
/// (<c>ResourceStore.HandleWaypointDelta</c>) can never drift on
/// what they lift - a future fourth MOB field added to
/// <see cref="GeoJsonProperties"/> only needs one edit here.</summary>
public static class SignalkWaypointExtensions
{
    /// <summary>Lift description + MOB metadata out of
    /// <see cref="GeoJsonFeature.Properties"/> onto the flat
    /// <see cref="SignalkWaypoint"/> projection fields. Treats an
    /// empty description as null so the popup-Edit textarea shows
    /// its placeholder rather than an empty input. Idempotent and
    /// safe to call after either deserialise path.</summary>
    public static void LiftFromFeatureProperties(this SignalkWaypoint wp)
    {
        var props = wp.Feature?.Properties;
        // Description: empty string is the absent-description value
        // GeoJsonBuilder.FeatureBody emits on Create; treat it as null
        // so the textarea placeholder ("Description (optional)")
        // surfaces instead of an empty input.
        var desc = props?.Description;
        wp.Description = string.IsNullOrEmpty(desc) ? null : desc;
        wp.IsMob = props?.IsMob == true;
        wp.IsMobActive = props?.IsMobActive == true;
        wp.MobAlarmId = props?.MobAlarmId;
    }
}
