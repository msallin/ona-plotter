using System.Text.Json.Serialization;
using OnaPlotter.Models;

namespace OnaPlotter.Services.Json;

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for the named
/// DTO types we deserialise (or round-trip) at hot or trim-sensitive
/// call sites. Each type listed here gets a compile-time-generated
/// converter pair; calls of the form
/// <c>JsonSerializer.Deserialize(span, OnaJsonContext.Default.SignalkDelta)</c>
/// skip the reflection-based serializer entirely.
/// <para>
/// Why this exists:
/// </para>
/// <list type="number">
///   <item><description><b>Hot path</b>:
///     <see cref="SignalkDelta"/> is deserialised once per WebSocket
///     frame (every SK delta on a steady feed). The reflection
///     serializer's per-type metadata cache is fast on warm runs
///     but still costs allocation; the source-gen converter is a
///     hand-written switch on property name, no reflection.</description></item>
///   <item><description><b>Trim-blocker</b>: enabling
///     <c>TrimMode=full</c> (see docs/research-trimmode.md) requires
///     every reflection-based <c>Deserialize&lt;T&gt;</c> to either
///     move to source-gen or carry a <c>[DynamicDependency]</c> hint.
///     Source-gen is the canonical fix; this context covers the
///     named-type deserialise sites. Anonymous-type serialisations
///     (Map.razor.cs share blobs, Resources.razor share blobs,
///     ResourceExporter feature payloads) still go through the
///     reflection path; converting them needs replacing the
///     anon-record-builder with a named record per shape, which is
///     a separate refactor.</description></item>
///   <item><description><b>Bundle size</b>: when every
///     <c>Deserialize&lt;T&gt;</c> uses source-gen, the linker can
///     drop large parts of <c>System.Text.Json.Reflection</c> and
///     friends. Concrete win measured at TrimMode flip time;
///     the doc audit estimates ~50 KB.</description></item>
/// </list>
/// <para>
/// Adding a new type: drop a <c>[JsonSerializable(typeof(T))]</c>
/// line below and use <c>OnaJsonContext.Default.T</c> (where
/// <c>T</c> is the property name the analyser generates) at the
/// call site. The compile fails with a clear message when a type
/// is referenced in a typed Deserialize but not registered here.
/// </para>
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
internal partial class OnaJsonContext : JsonSerializerContext
{
}
