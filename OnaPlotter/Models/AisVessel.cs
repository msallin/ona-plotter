using System.Text.Json;

namespace OnaPlotter.Models;

/// <summary>Where a tracked target originated. Kept on the target so the
/// map can render AIS and radar distinguishably and so features that only
/// apply to one source (external name lookup, buddy tagging) can be
/// skipped for the other.</summary>
public enum TargetSource
{
    /// <summary>AIS vessel, context format <c>vessels.urn:mrn:imo:mmsi:NNN</c>.</summary>
    Ais,
    /// <summary>Radar ARPA target from Mayara, synthesised context
    /// <c>radar.&lt;radarId&gt;.&lt;targetId&gt;</c>.</summary>
    Radar,
}

/// <summary>
/// Mutable state for a single tracked target. Originally built for AIS
/// vessels; now doubles as the state holder for radar ARPA targets too
/// (they share the same fields the alarm pipeline cares about).
/// Populated from SignalK delta updates.
/// </summary>
public sealed class AisVessel
{
    public string Context { get; }
    public TargetSource Source { get; set; } = TargetSource.Ais;
    public string? Name { get; set; }
    public string? Mmsi { get; set; }
    public string? Callsign { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public double? Heading { get; set; }
    public double? CourseOverGround { get; set; }
    public double? SpeedOverGround { get; set; }
    public string? ShipType { get; set; }

    /// <summary>
    /// True when the vessel is in the captain's buddy list (populated from
    /// sbender9/signalk-buddylist-plugin, either via the REST seed at startup
    /// or the `buddy` path on the delta stream). Buddies are exempt from the
    /// CPA alarm and get a distinct icon on the map.
    /// </summary>
    public bool IsBuddy { get; set; }

    public DateTime LastSeen { get; set; }

    public AisVessel(string context)
    {
        Context = context;
        LastSeen = DateTime.UtcNow;
    }

    /// <summary>
    /// Applies a SignalK path/value pair. Returns true if the value was recognized.
    /// </summary>
    public bool Apply(string path, object? rawValue)
    {
        LastSeen = DateTime.UtcNow;

        // Empty-path deltas carry an object payload that's a partial vessel
        // identity snapshot, e.g. { name: "SALTY BREEZE", mmsi: "211...",
        // communication: { callsignVhf: "..." } }. SK servers emit this
        // when AIS message type 5 (static data) arrives for a vessel that
        // previously only had position updates. Flatten and recurse so
        // names / MMSIs show up without waiting for a separate per-path
        // delta that may never come. User-visible effect: fixes the
        // "ship names sometimes missing" regression.
        if (path.Length == 0 && rawValue is JsonElement identityEl
            && identityEl.ValueKind == JsonValueKind.Object)
        {
            return FlattenAndApply("", identityEl, depth: 0);
        }

        switch (path)
        {
            // AIS uses the full SignalK path names; Mayara radar targets use
            // the short leaf names (position / course / speed) under their
            // own radars.*.targets.* subtree. Accept both for the same fields
            // so every consumer downstream sees a uniform target shape.
            case "navigation.position":
            case "position":
                if (rawValue is JsonElement posEl && posEl.ValueKind == JsonValueKind.Object
                    && posEl.TryGetProperty("latitude", out var lat)
                    && posEl.TryGetProperty("longitude", out var lon)
                    && lat.ValueKind == JsonValueKind.Number
                    && lon.ValueKind == JsonValueKind.Number)
                {
                    Latitude = lat.GetDouble();
                    Longitude = lon.GetDouble();
                    return true;
                }
                return false;

            case "navigation.headingTrue":
                Heading = ToDouble(rawValue);
                return Heading is not null;

            case "navigation.courseOverGroundTrue":
            case "course":
                CourseOverGround = ToDouble(rawValue);
                return CourseOverGround is not null;

            case "navigation.speedOverGround":
            case "speed":
                SpeedOverGround = ToDouble(rawValue);
                return SpeedOverGround is not null;

            case "name":
                {
                    var parsed = TryGetString(rawValue);
                    if (parsed is null) return false;      // null delta must NOT overwrite
                    Name = parsed;
                    return true;
                }

            case "mmsi":
                {
                    var parsed = TryGetString(rawValue);
                    if (parsed is null) return false;
                    Mmsi = parsed;
                    return true;
                }

            case "communication.callsignVhf":
                {
                    var parsed = TryGetString(rawValue);
                    if (parsed is null) return false;
                    Callsign = parsed;
                    return true;
                }

            case "design.aisShipType":
                if (rawValue is JsonElement typeEl)
                    ShipType = typeEl.TryGetProperty("name", out var n) ? n.GetString() : typeEl.ToString();
                else
                    ShipType = rawValue?.ToString();
                return true;

            case "buddy":
                bool newBuddy = rawValue switch
                {
                    bool b => b,
                    JsonElement bEl => bEl.ValueKind == JsonValueKind.True,
                    _ => false
                };
                if (newBuddy == IsBuddy) return false;
                IsBuddy = newBuddy;
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Extracts MMSI from a context string like "vessels.urn:mrn:imo:mmsi:211234567".
    /// </summary>
    public static string? ExtractMmsi(string context)
    {
        // Example: "vessels.urn:mrn:imo:mmsi:211234567"
        const string prefix = "mmsi:";
        int idx = context.IndexOf(prefix, StringComparison.Ordinal);
        if (idx < 0) return null;
        return context[(idx + prefix.Length)..];
    }

    private static double? ToDouble(object? raw)
    {
        if (raw is null) return null;
        if (raw is double d) return d;
        if (raw is JsonElement je && je.ValueKind == JsonValueKind.Number) return je.GetDouble();
        return null;
    }

    // Returns the string value if rawValue is genuinely a string; null
    // for null / undefined / non-string shapes. Used by identity fields
    // (name / mmsi / callsign) so a null-valued delta from the flatten
    // path doesn't clobber existing good data. SK servers emit
    // { name: null, mmsi: "..." } occasionally on partial identity
    // updates, which the previous .ToString() fallback turned into
    // the literal string "null".
    private static string? TryGetString(object? raw)
    {
        if (raw is null) return null;
        if (raw is string s) return s;
        if (raw is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.String) return je.GetString();
            if (je.ValueKind == JsonValueKind.Number) return je.GetRawText();
            // Null / Undefined / objects / arrays all fall through as null.
        }
        return null;
    }

    // Max depth the identity-flattener will recurse into. SK identity
    // payloads in the wild have been observed at depth 1 (communication.
    // callsignVhf, design.aisShipType) and occasionally depth 2 (future
    // message types, rare). Beyond that is either a malformed payload
    // or a server doing something unusual; clamping avoids a crafted
    // or buggy delta driving unbounded recursion.
    private const int MaxIdentityFlattenDepth = 4;

    private bool FlattenAndApply(string prefix, JsonElement obj, int depth)
    {
        if (depth > MaxIdentityFlattenDepth) return false;
        bool anyChange = false;
        foreach (var prop in obj.EnumerateObject())
        {
            string fullPath = prefix.Length == 0 ? prop.Name : $"{prefix}.{prop.Name}";
            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                // Try the full dotted path first -- some leaves in the
                // switch take an object (design.aisShipType = { id, name }).
                // Then recurse so deeper-nested scalars (rare but possible)
                // also get dispatched.
                anyChange |= Apply(fullPath, prop.Value);
                anyChange |= FlattenAndApply(fullPath, prop.Value, depth + 1);
            }
            else
            {
                anyChange |= Apply(fullPath, prop.Value);
            }
        }
        return anyChange;
    }
}
