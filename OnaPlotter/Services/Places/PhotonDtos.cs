using System.Text.Json.Serialization;

namespace OnaPlotter.Services.Places;

/// <summary>
/// Wire-shape DTOs for the Photon geocoder response
/// (<c>https://photon.komoot.io/api</c>). Photon emits a GeoJSON
/// FeatureCollection; we only consume the fields we render in the
/// dropdown row so unknown / extra Photon properties (osm_id,
/// extent bbox, postcode, ...) ignore-deserialise without binding
/// to the model.
///
/// <para>Internal records: the type surface is implementation
/// detail of <see cref="PhotonPlaceSearchService"/>; nothing
/// outside Places/ should refer to it. Source-gen registrations in
/// <c>OnaJsonContext</c> make these trim-safe.</para>
/// </summary>
internal sealed record PhotonResponse(
    [property: JsonPropertyName("features")] PhotonFeature[]? Features);

internal sealed record PhotonFeature(
    [property: JsonPropertyName("geometry")] PhotonGeometry? Geometry,
    [property: JsonPropertyName("properties")] PhotonProperties? Properties);

internal sealed record PhotonGeometry(
    [property: JsonPropertyName("coordinates")] double[]? Coordinates);

/// <summary>
/// The fields we actually render. Photon decorates each feature
/// with a deeper graph (extent bbox, postcode, district, etc.) but
/// the helm-facing dropdown only needs name + locality + country
/// + type (so a "city" row can read differently from a "marina"
/// or a "harbour" row).
/// </summary>
internal sealed record PhotonProperties(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("city")] string? City,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("country")] string? Country,
    [property: JsonPropertyName("countrycode")] string? CountryCode,
    [property: JsonPropertyName("type")] string? Type);
