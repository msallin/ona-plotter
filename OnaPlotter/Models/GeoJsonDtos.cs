using System.Text.Json.Serialization;

namespace OnaPlotter.Models;

// =====================================================================
// GeoJSON Feature shapes used by ResourceExporter (per-resource exports
// to RFC 7946) and by the Map / Resources share-blob handlers (system-
// share-sheet payloads). Replacing the earlier
// `JsonSerializer.Serialize(new {...})` anonymous shapes so source-gen
// can take the serialiser path; see OnaGeoJsonContext + the F2 trim
// audit in docs/research-trimmode.md.
//
// Two flavours of properties bags are needed:
//
// - Export features (Route / Waypoint / Note / Trip / Region): null
//   fields stay in the output. Matches the previous PrettyJson behaviour
//   on ResourceExporter.cs (no `DefaultIgnoreCondition` set).
// - Share features (Map.razor.cs + Resources.razor share-blobs): nullable
//   fields with `[JsonIgnore(Condition = WhenWritingNull)]` so a missing
//   createdAt / name elides. Matches the previous per-call-site option
//   (`DefaultIgnoreCondition = WhenWritingNull`).
//
// Internal records, exposed to the test project via the existing
// InternalsVisibleTo entry on OnaPlotter.csproj.
// =====================================================================

// ------------- Geometry shapes --------------------------------------

internal sealed record GeoJsonPointGeometry(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("coordinates")] double[] Coordinates);

internal sealed record GeoJsonLineStringGeometry(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("coordinates")] double[][] Coordinates);

internal sealed record GeoJsonPolygonGeometry(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("coordinates")] double[][][] Coordinates);

internal sealed record GeoJsonMultiPolygonGeometry(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("coordinates")] double[][][][] Coordinates);

// ------------- Export-feature properties bags ------------------------
//
// No JsonIgnore on nullable fields: matches ResourceExporter's PrettyJson
// behaviour where `distance: null` round-trips literally. A
// docs-cosmetic fix that elides nulls would change the wire shape;
// out of scope for this batch.

internal sealed record GeoJsonNameProperties(
    [property: JsonPropertyName("name")] string Name);

internal sealed record GeoJsonNameDescProperties(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description);

internal sealed record GeoJsonTitleDescProperties(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description);

internal sealed record GeoJsonRouteProperties(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("distance")] double? Distance);

internal sealed record GeoJsonTripProperties(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("startUtc")] string StartUtc,
    [property: JsonPropertyName("endUtc")] string EndUtc,
    [property: JsonPropertyName("durationSec")] double DurationSec,
    [property: JsonPropertyName("distanceMeters")] double DistanceMeters,
    [property: JsonPropertyName("sogAvgMs")] double? SogAvgMs,
    [property: JsonPropertyName("sogMaxMs")] double? SogMaxMs,
    [property: JsonPropertyName("sogMinMs")] double? SogMinMs,
    [property: JsonPropertyName("twsAvgMs")] double? TwsAvgMs,
    [property: JsonPropertyName("pointCount")] int PointCount,
    [property: JsonPropertyName("isStationary")] bool IsStationary);

// ------------- Export Feature wrappers ------------------------------
//
// One record per (geometry shape, properties bag) pair. Source-gen
// requires concrete types so the analyser can emit a tailored
// converter; using a generic GeoJsonFeature<TProps, TGeo> would either
// require registering each instantiation or fall back to reflection.

internal sealed record GeoJsonRouteFeature(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("properties")] GeoJsonRouteProperties Properties,
    [property: JsonPropertyName("geometry")] GeoJsonLineStringGeometry Geometry);

internal sealed record GeoJsonWaypointFeature(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("properties")] GeoJsonNameProperties Properties,
    [property: JsonPropertyName("geometry")] GeoJsonPointGeometry Geometry);

internal sealed record GeoJsonNoteFeature(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("properties")] GeoJsonTitleDescProperties Properties,
    [property: JsonPropertyName("geometry")] GeoJsonPointGeometry Geometry);

internal sealed record GeoJsonTripFeature(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("properties")] GeoJsonTripProperties Properties,
    [property: JsonPropertyName("geometry")] GeoJsonLineStringGeometry Geometry);

internal sealed record GeoJsonRegionPolygonFeature(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("properties")] GeoJsonNameDescProperties Properties,
    [property: JsonPropertyName("geometry")] GeoJsonPolygonGeometry Geometry);

internal sealed record GeoJsonRegionMultiPolygonFeature(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("properties")] GeoJsonNameDescProperties Properties,
    [property: JsonPropertyName("geometry")] GeoJsonMultiPolygonGeometry Geometry);

// ------------- Share-feature shapes ---------------------------------
//
// Distinct from the export shapes: the share blob carries createdAt
// (so the receiving app shows "shared at X"), and per-field
// JsonIgnore preserves the elision behaviour of the previous
// `DefaultIgnoreCondition = WhenWritingNull` option on the call sites
// in Map.razor.cs / Resources.razor.
//
// Wire-order note: the share Feature ctor is (type, geometry,
// properties), the export Feature ctors above are
// (type, properties, geometry). Both shapes match the previous
// anonymous-type literal layout exactly -- preserving byte-level
// parity with what the helm previously sent / shared. Don't
// "normalise" the two to the same order: GeoJSON consumers parse
// by key, but the exact byte sequence is what was field-tested
// before this migration.

internal sealed record GeoJsonShareWaypointProperties(
    [property: JsonPropertyName("name"),
        JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name,
    [property: JsonPropertyName("createdAt"),
        JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CreatedAt);

internal sealed record GeoJsonShareNoteProperties(
    [property: JsonPropertyName("title"),
        JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Title,
    [property: JsonPropertyName("description"),
        JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description,
    [property: JsonPropertyName("createdAt"),
        JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CreatedAt);

internal sealed record GeoJsonShareWaypointFeature(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("geometry")] GeoJsonPointGeometry Geometry,
    [property: JsonPropertyName("properties")] GeoJsonShareWaypointProperties Properties);

internal sealed record GeoJsonShareNoteFeature(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("geometry")] GeoJsonPointGeometry Geometry,
    [property: JsonPropertyName("properties")] GeoJsonShareNoteProperties Properties);
