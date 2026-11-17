using System.Text.Json;
using OnaPlotter.Models;
using OnaPlotter.Utilities;

namespace OnaPlotter.Services.Signalk;

/// <summary>
/// Routes other-vessel deltas and radar ARPA targets into
/// <see cref="AisStore"/>. Radar targets arrive under the self context
/// (<c>radars.&lt;rid&gt;.targets.&lt;tid&gt;...</c>) and are stored under a
/// synthesised <c>radar.&lt;rid&gt;.&lt;tid&gt;</c> context so the alarm /
/// CPA pipeline treats them uniformly with AIS vessels.
/// </summary>
internal sealed class AisDeltaHandler
{
    private readonly AisStore _ais;

    public AisDeltaHandler(AisStore ais)
    {
        _ais = ais;
    }

    /// <summary>Applies a delta whose context is another vessel.</summary>
    public void Apply(SignalkDelta delta)
    {
        foreach (var update in delta.Updates!)
        {
            if (update.Values is null)
                continue;

            // Parse the wire timestamp once per update (e.g.
            // "2024-01-01T12:00:00.000Z"). When absent or malformed, leave
            // null so AisVessel.Apply falls back to delta-arrival time.
            DateTime? skTs = SignalkTimestamp.TryParseUtc(update.Timestamp, out var parsedTs)
                ? parsedTs
                : null;

            foreach (var val in update.Values)
            {
                if (val.Path is null)
                    continue;

                _ais.Apply(delta.Context!, val.Path, val.Value, skTs);
            }
        }
    }

    /// <summary>
    /// Routes a radar-target delta. Two wire shapes seen in the wild,
    /// both handled:
    /// <list type="number">
    ///   <item>Spec (v3.1): <c>radars.&lt;rid&gt;.targets.&lt;tid&gt;</c>
    ///     with the whole target object (id, status, position, motion) as
    ///     value. <c>value: null</c> signals target deletion.</item>
    ///   <item>Per-field (early Mayara):
    ///     <c>radars.&lt;rid&gt;.targets.&lt;tid&gt;.&lt;field&gt;</c>
    ///     with per-field values (position / course / speed).</item>
    /// </list>
    /// Malformed / unknown shapes drop silently - the receive loop must
    /// stay alive no matter what the server emits.
    /// </summary>
    public void ApplyRadar(string path, DeltaValue value)
    {
        if (TryParseRadarTargetPath(path) is not var (radarId, targetId, field)) return;
        string ctx = $"{AisStore.RadarContextPrefix}{radarId}.{targetId}";

        // Spec shape: value is the whole target object (or null for
        // deletion). Field is null because the path stops at target id.
        if (field is null)
        {
            if (value.IsNull)
            {
                _ais.RemoveContext(ctx);
                return;
            }
            if (value.TryGetElement(out var targetEl) && targetEl.ValueKind == JsonValueKind.Object)
            {
                // Translate into the field-keyed shape AisVessel.Apply
                // understands. Position first so the vessel has
                // coordinates on the first delta; motion fields follow
                // and update what's already in the store.
                if (targetEl.TryGetProperty("position", out var pos)
                    && pos.ValueKind == JsonValueKind.Object)
                {
                    _ais.Apply(ctx, "position", pos);
                }
                if (targetEl.TryGetProperty("motion", out var motion)
                    && motion.ValueKind == JsonValueKind.Object)
                {
                    if (motion.TryGetProperty("course", out var cog)
                        && cog.ValueKind == JsonValueKind.Number)
                        _ais.Apply(ctx, "course", cog);
                    if (motion.TryGetProperty("speed", out var sog)
                        && sog.ValueKind == JsonValueKind.Number)
                        _ais.Apply(ctx, "speed", sog);
                }
            }
            return;
        }

        // Per-field shape: value is the field's primitive; pass through.
        _ais.Apply(ctx, field, value);
    }

    /// <summary>
    /// Parses a radar-target delta path. Splits on the literal
    /// <c>.targets.</c> separator so radar ids containing dots
    /// (IPv4-style hardware identifiers) still parse:
    /// <code>
    /// "radars.r1.targets.t1"                       -> ("r1", "t1", null)       spec shape
    /// "radars.r1.targets.t1.position"              -> ("r1", "t1", "position")
    /// "radars.192.168.1.42.targets.T001.position"  -> ("192.168.1.42", "T001", "position")
    /// "radars.r1.status", "radars..targets.t1"     -> null
    /// </code>
    /// </summary>
    internal static (string radarId, string targetId, string? field)? TryParseRadarTargetPath(string path)
    {
        if (!path.StartsWith("radars.", StringComparison.Ordinal)) return null;

        const string sep = ".targets.";
        int sepIdx = path.IndexOf(sep, StringComparison.Ordinal);
        if (sepIdx < 0) return null;

        string radarId = path.Substring("radars.".Length, sepIdx - "radars.".Length);
        if (radarId.Length == 0) return null;

        string after = path[(sepIdx + sep.Length)..];
        if (after.Length == 0) return null;

        int dot = after.IndexOf('.');
        if (dot < 0)
        {
            // Spec shape: path stops at the target id; value carries
            // the whole target object (or null for delete).
            return (radarId, after, null);
        }
        if (dot == 0 || dot == after.Length - 1) return null;

        string targetId = after[..dot];
        string field = after[(dot + 1)..];
        return (radarId, targetId, field);
    }
}
