using System.Text.Json.Serialization;
using OnaPlotter.Models;

namespace OnaPlotter.Services.Json;

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for the named
/// DTO types and primitive shapes used at hot or trim-sensitive
/// JSON call sites. Each type listed here gets a compile-time-
/// generated converter pair; calls of the form
/// <c>JsonSerializer.Deserialize(span, OnaJsonContext.Default.SignalkDelta)</c>
/// skip reflection entirely.
///
/// <para>
/// <b>Adding a new type</b> (the common contributor task): drop a
/// <c>[JsonSerializable(typeof(T))]</c> line below and use
/// <c>OnaJsonContext.Default.T</c> (where <c>T</c> is the property
/// name the analyser generates) at the call site. The compile fails
/// with a clear message when a type is referenced in a typed
/// Deserialize but not registered here. Also add a round-trip case
/// to <c>OnaJsonContextTests</c> so a silent regression on the
/// converter (e.g. a future analyser upgrade dropping a property
/// attribute) surfaces in CI.
/// </para>
///
/// <para>
/// <b>Why this exists</b> (the longer story):
/// </para>
/// <list type="number">
///   <item><description><b>Hot path</b>:
///     <see cref="SignalkDelta"/> is deserialised once per WebSocket
///     frame (every SK delta on a steady feed). The runtime
///     reflection serializer's per-type metadata cache is fast on
///     warm runs but still costs allocation; the source-gen
///     converter is a hand-written switch on property name, no
///     reflection.</description></item>
///   <item><description><b>Trim-readiness</b>: enabling
///     <c>TrimMode=full</c> (see docs/research-trimmode.md) requires
///     every <c>Serialize&lt;T&gt;</c> / <c>Deserialize&lt;T&gt;</c>
///     to either move to source-gen or carry a
///     <c>[DynamicDependency]</c> hint. The application code is
///     fully migrated as of F2: this context covers wire-protocol
///     DTOs (compact output) and primitive shapes used at JS-interop
///     literal-embed sites; <see cref="OnaGeoJsonContext"/> covers
///     the helm-facing GeoJSON exports + share blobs (indented
///     output).</description></item>
///   <item><description><b>Bundle size</b>: source-gen lets the
///     linker drop large parts of <c>System.Text.Json.Reflection</c>
///     once <c>TrimMode=full</c> ships. Audit estimates ~50 KB.</description></item>
/// </list>
/// </summary>
// PropertyNameCaseInsensitive: matches the previous AuthApi behaviour
// (the only call site that explicitly enabled it). Other types in the
// context all carry [JsonPropertyName] attributes that pin the
// canonical name, so the case-insensitive fallback is a no-op for
// them in practice; setting it once here keeps the policy uniform
// across all generated converters.
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SignalkDelta))]
[JsonSerializable(typeof(LoginStatus))]
[JsonSerializable(typeof(RouteDraft))]
[JsonSerializable(typeof(SnoozedTarget[]))]
[JsonSerializable(typeof(OnaPlotter.Services.Mob.PendingRaise[]))]
[JsonSerializable(typeof(OnaPlotter.Services.Mob.ResolvedMobPosition[]))]
[JsonSerializable(typeof(SignalkSubscribeRequest))]
[JsonSerializable(typeof(SignalkUnsubscribeRequest))]
// Primitive / array types used at the History.razor JS-interop literal
// embedding sites. Registering them here lets each Serialize call go
// through the typed source-gen overload instead of the reflection-based
// generic; cuts the trim warnings the otherwise pristine F2/3 surface
// would otherwise emit at TrimMode=full.
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(double[]))]
[JsonSerializable(typeof(double[][]))]
[JsonSerializable(typeof(List<OnaPlotter.Utilities.HistorySegmentRender.SegmentPayload>))]
// Place-search wire shapes. PhotonResponse is the primary geocoder
// client's deserialise target; NominatimResult[] is the fallback
// (Phase 4) geocoder's; CachedQuery[] is the localStorage round-trip
// for PlaceSearchCache.
[JsonSerializable(typeof(OnaPlotter.Services.Places.PhotonResponse))]
[JsonSerializable(typeof(OnaPlotter.Services.Places.NominatimResult[]))]
[JsonSerializable(typeof(OnaPlotter.Services.Places.CachedQuery[]))]
internal partial class OnaJsonContext : JsonSerializerContext
{
}
