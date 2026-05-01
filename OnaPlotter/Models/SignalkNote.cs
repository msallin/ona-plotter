using System.Text.Json.Serialization;

namespace OnaPlotter.Models;

/// <summary>
/// A SignalK note resource: a short text annotation anchored at a lat/lon
/// on the chart. Unlike routes and waypoints, notes don't use a GeoJSON
/// Feature wrapper -- the SignalK spec uses a bare <c>position</c> object.
/// See https://github.com/panaaj/sk-types/blob/master/src/resources/index.ts
/// for the authoritative Note shape used by Freeboard-SK and the default
/// resources-fs provider.
/// </summary>
public sealed class SignalkNote
{
    /// <summary>Server-assigned resource ID. Only set on notes loaded from
    /// the server; ignored in POST payloads.</summary>
    [JsonIgnore]
    public string Id { get; set; } = "";

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("position")]
    public NotePosition? Position { get; set; }

    [JsonPropertyName("mimeType")]
    public string? MimeType { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>UTC instant the note was first PUT to the server. The
    /// SignalK Note schema doesn't include this -- it's a custom field
    /// OnaPlotter writes on Create + preserves on Update. The
    /// resources-fs provider (and most third-party providers) round-trip
    /// arbitrary JSON fields on the resource body unchanged, so it's a
    /// reasonably-portable place to stash the timestamp without
    /// inventing a sidecar resource. Notes created by other clients
    /// (Freeboard, kip, etc.) won't have this populated; the popup
    /// renders a dash for those rather than a fake "now".</summary>
    [JsonPropertyName("createdAt")]
    public DateTime? CreatedAt { get; set; }
}

public sealed class NotePosition
{
    [JsonPropertyName("latitude")]
    public double Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public double Longitude { get; set; }
}
