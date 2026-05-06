using System.Text.Json.Serialization;

namespace OnaPlotter.Services.Places;

/// <summary>
/// Wire-shape DTO for one Nominatim
/// (<c>https://nominatim.openstreetmap.org/search</c>) result row.
/// Nominatim returns a flat JSON array of these (NOT a GeoJSON
/// FeatureCollection like Photon), so the deserialise target is
/// <see cref="NominatimResult"/><c>[]</c>.
///
/// <para>Lat / Lon are emitted as <b>strings</b> by the public
/// instance even with <c>format=jsonv2</c>; the parser converts to
/// <see cref="double"/> with <see cref="System.Globalization.CultureInfo.InvariantCulture"/>
/// so a server-locale that uses a comma decimal doesn't round-trip
/// us into NaN.</para>
///
/// <para>Only the fields used by the dropdown row are bound; the
/// rest of Nominatim's response (place_id, licence, osm_type, bbox,
/// importance, ...) ignore-deserialises. <see cref="DisplayName"/>
/// is the long human-friendly label ("Berlin, 10117, Germany"); we
/// use it as the meta-line and synthesise a short
/// <see cref="PlaceResult.Name"/> from <see cref="Name"/> when
/// present, falling back to the head of <see cref="DisplayName"/>.</para>
/// </summary>
internal sealed record NominatimResult(
    [property: JsonPropertyName("lat")] string? Lat,
    [property: JsonPropertyName("lon")] string? Lon,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("display_name")] string? DisplayName,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("addresstype")] string? AddressType);
