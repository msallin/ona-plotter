using System.Text.Json.Serialization;
using OnaPlotter.Models;

namespace OnaPlotter.Services.Json;

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for the
/// helm-facing GeoJSON exports + share blobs. Distinct from
/// <see cref="OnaJsonContext"/> because the export pipeline wants
/// indented output (helm sometimes opens the .geojson file in a text
/// editor before sharing) while the wire-protocol DTOs in OnaJsonContext
/// stay compact.
/// <para>
/// Per-record <c>[JsonIgnore(Condition = WhenWritingNull)]</c> on
/// share-blob nullable fields reproduces the earlier per-call-site
/// <c>DefaultIgnoreCondition = WhenWritingNull</c> option that the
/// reflection serializer used. Export-feature properties don't carry
/// the attribute, matching the previous PrettyJson behaviour where
/// nulls were emitted literally.
/// </para>
/// <para>
/// Adding a new export shape: drop a <c>[JsonSerializable(typeof(T))]</c>
/// line and add the matching wire-shape test in
/// <c>OnaJsonContextTests</c> (which covers both contexts).
/// </para>
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(GeoJsonRouteFeature))]
[JsonSerializable(typeof(GeoJsonWaypointFeature))]
[JsonSerializable(typeof(GeoJsonNoteFeature))]
[JsonSerializable(typeof(GeoJsonTripFeature))]
[JsonSerializable(typeof(GeoJsonRegionPolygonFeature))]
[JsonSerializable(typeof(GeoJsonRegionMultiPolygonFeature))]
[JsonSerializable(typeof(GeoJsonShareWaypointFeature))]
[JsonSerializable(typeof(GeoJsonShareNoteFeature))]
internal partial class OnaGeoJsonContext : JsonSerializerContext
{
}
