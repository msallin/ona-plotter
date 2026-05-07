using System.Text.Json.Serialization;

namespace OnaPlotter.Services.Pois;

/// <summary>
/// Wire shapes for the Overpass API JSON response. Documented at
/// <c>https://wiki.openstreetmap.org/wiki/Overpass_API/Overpass_QL</c>.
///
/// <para>An Overpass request with <c>[out:json]</c> + <c>out center
/// tags</c> returns: a top-level object with version / generator /
/// osm3s metadata plus an <c>elements</c> array. Each element has a
/// <c>type</c> ("node" | "way" | "relation"), an <c>id</c>, a
/// <c>tags</c> dict, and either direct <c>lat</c> / <c>lon</c>
/// (nodes) or a <c>center.lat</c> / <c>center.lon</c> (ways and
/// relations under <c>out center</c>).</para>
///
/// <para>Only the fields the parser reads are declared here.
/// Unknown fields are ignored on deserialise (the default
/// System.Text.Json behaviour) so a future Overpass schema addition
/// doesn't break the client.</para>
/// </summary>
public sealed record OverpassResponse
{
    [JsonPropertyName("elements")]
    public OverpassElement[]? Elements { get; init; }
}

/// <summary>
/// One element in <see cref="OverpassResponse.Elements"/>. <see cref="Lat"/>
/// / <see cref="Lon"/> are populated for nodes; <see cref="Center"/>
/// is populated for ways / relations under <c>out center</c>; the
/// parser falls through both so a single record handles every shape.
/// </summary>
public sealed record OverpassElement
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("lat")]
    public double? Lat { get; init; }

    [JsonPropertyName("lon")]
    public double? Lon { get; init; }

    [JsonPropertyName("center")]
    public OverpassCenter? Center { get; init; }

    [JsonPropertyName("tags")]
    public Dictionary<string, string>? Tags { get; init; }
}

/// <summary>Synthetic centre injected by Overpass under <c>out center</c>
/// for non-point elements (ways, relations).</summary>
public sealed record OverpassCenter
{
    [JsonPropertyName("lat")]
    public double Lat { get; init; }

    [JsonPropertyName("lon")]
    public double Lon { get; init; }
}
