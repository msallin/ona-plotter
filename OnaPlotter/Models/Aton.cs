using System.Text.Json;

namespace OnaPlotter.Models;

/// <summary>
/// One Aid to Navigation: a fixed or floating navigational aid (buoy,
/// beacon, lighthouse, racon) broadcast over AIS Type 21 and republished
/// by SignalK under the <c>atons.*</c> context.
/// <para>
/// Modelled after the SignalK shape so populating it from raw deltas is
/// almost path-for-path. Frequently-rendered fields (position, name,
/// type, virtual) are typed; everything else lands in
/// <see cref="Properties"/> for forward compatibility - a plugin can
/// publish new paths and we'll show them in the popover without code
/// changes.
/// </para>
/// </summary>
public sealed class Aton
{
    /// <summary>Full SignalK context, e.g.
    /// <c>atons.urn:mrn:imo:mmsi:992111234</c>. Used as the dictionary
    /// key in <see cref="OnaPlotter.Services.AtonStore"/>.</summary>
    public string Context { get; }

    /// <summary>9-digit MMSI extracted from the context. AtoNs use the
    /// 99x prefix per ITU-R M.585. Null on contexts that don't follow
    /// the urn-mrn-imo-mmsi shape.</summary>
    public string? Mmsi { get; set; }

    /// <summary>Plugin-published name string, e.g. "BUOY 17",
    /// "EAST CARDINAL". Falls back to MMSI on the popover when missing.</summary>
    public string? Name { get; set; }

    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    /// <summary>Numeric AtoN type code per AIS Type 21 (1..31).
    /// 9-12 = real cardinal N/E/S/W; 13-14 = real port/starboard
    /// lateral; 20-25 = virtual cardinal/lateral; 28 = isolated
    /// danger; 29 = safe water; 30 = special; -1 = unknown / base
    /// station.</summary>
    public int? TypeId { get; set; }

    /// <summary>Free-text type label as published by the plugin
    /// (e.g. "Cardinal Mark N", "Lateral Port"). Used as a fallback
    /// in the popover when the numeric type doesn't map to a known
    /// icon.</summary>
    public string? TypeName { get; set; }

    /// <summary>Virtual AtoNs (broadcast by AIS without a physical
    /// mark in the water - e.g. wreck warnings) render with a dashed
    /// outline so the helm doesn't go looking for an actual buoy.</summary>
    public bool? Virtual { get; set; }

    /// <summary>Forward-compat catch-all: any path we don't model
    /// explicitly lands here keyed by the SignalK path. Lets a popover
    /// show "raw paths" for diagnostics without needing a code change
    /// every time a plugin adds a field.</summary>
    public Dictionary<string, object?> Properties { get; } = new(StringComparer.Ordinal);

    public DateTime LastSeen { get; set; }

    public Aton(string context)
    {
        Context = context;
        LastSeen = DateTime.UtcNow;
        Mmsi = ExtractMmsi(context);
    }

    /// <summary>Applies a SignalK delta path/value pair. Returns true
    /// when the entry actually changed (caller propagates an update
    /// event). Returns false on no-op deltas (idempotent re-application
    /// of the same value) so the UI doesn't redraw on every tick.</summary>
    public bool Apply(string path, object? rawValue)
    {
        LastSeen = DateTime.UtcNow;

        // Empty-path delta carries an object payload that's a partial
        // identity / position bundle: { name, mmsi, atonType:{id,name},
        // virtual, navigation: { position: { latitude, longitude } } }.
        // Plugins emit this on the first delta after the AIS Type 21
        // arrives, then sometimes never resend the per-leaf paths -
        // flatten so we don't have to wait for individual updates that
        // may never come. A non-object empty-path delta (rare, e.g.
        // a plugin error) is silently ignored rather than stashed
        // under Properties[""] which would be diagnostic noise.
        if (path.Length == 0)
        {
            if (rawValue is JsonElement bundle
                && bundle.ValueKind == JsonValueKind.Object)
            {
                return ApplyIdentityBundle(bundle);
            }
            return false;
        }

        switch (path)
        {
            case "name":
                {
                    var v = AsString(rawValue);
                    if (Name == v) return false;
                    Name = v;
                    return true;
                }
            case "mmsi":
                {
                    var v = AsString(rawValue);
                    if (Mmsi == v) return false;
                    Mmsi = v;
                    return true;
                }
            case OnaPlotter.Utilities.SkPaths.Navigation.Position:
                return ApplyPosition(rawValue);
            case "atonType":
                return ApplyAtonType(rawValue);
            case "virtual":
                return ApplyVirtual(rawValue);
            default:
                // Stash unknown paths for the popover. Replace any
                // existing value - plugins re-publish on change.
                Properties[path] = rawValue;
                return true;
        }
    }

    private bool ApplyIdentityBundle(JsonElement bundle)
    {
        bool changed = false;
        if (bundle.TryGetProperty("name", out var n)
            && n.ValueKind == JsonValueKind.String)
        {
            var nameStr = n.GetString();
            if (Name != nameStr) { Name = nameStr; changed = true; }
        }
        if (bundle.TryGetProperty("mmsi", out var m)
            && m.ValueKind == JsonValueKind.String)
        {
            var mmsiStr = m.GetString();
            if (Mmsi != mmsiStr) { Mmsi = mmsiStr; changed = true; }
        }
        if (bundle.TryGetProperty("atonType", out var t))
        {
            changed |= ApplyAtonType(t);
        }
        if (bundle.TryGetProperty("virtual", out var virtualEl))
        {
            changed |= ApplyVirtual(virtualEl);
        }
        if (bundle.TryGetProperty("navigation", out var nav)
            && nav.ValueKind == JsonValueKind.Object
            && nav.TryGetProperty("position", out var pos))
        {
            changed |= ApplyPosition(pos);
        }
        return changed;
    }

    private bool ApplyPosition(object? rawValue)
    {
        if (rawValue is not JsonElement el || el.ValueKind != JsonValueKind.Object)
            return false;
        if (!el.TryGetProperty("latitude", out var latEl)
            || !el.TryGetProperty("longitude", out var lonEl))
            return false;
        if (latEl.ValueKind is not (JsonValueKind.Number)
            || lonEl.ValueKind is not (JsonValueKind.Number))
            return false;
        var lat = latEl.GetDouble();
        var lon = lonEl.GetDouble();
        bool changed = false;
        if (Latitude != lat) { Latitude = lat; changed = true; }
        if (Longitude != lon) { Longitude = lon; changed = true; }
        return changed;
    }

    private bool ApplyAtonType(object? rawValue)
    {
        if (rawValue is not JsonElement el) return false;
        // Object form: { id: 14, name: "Lateral Starboard" }
        if (el.ValueKind == JsonValueKind.Object)
        {
            bool changed = false;
            if (el.TryGetProperty("id", out var idEl)
                && idEl.ValueKind == JsonValueKind.Number)
            {
                var v = idEl.GetInt32();
                if (TypeId != v) { TypeId = v; changed = true; }
            }
            if (el.TryGetProperty("name", out var nameEl)
                && nameEl.ValueKind == JsonValueKind.String)
            {
                var v = nameEl.GetString();
                if (TypeName != v) { TypeName = v; changed = true; }
            }
            return changed;
        }
        // Bare-number form: just the id.
        if (el.ValueKind == JsonValueKind.Number)
        {
            var v = el.GetInt32();
            if (TypeId == v) return false;
            TypeId = v;
            return true;
        }
        return false;
    }

    private bool ApplyVirtual(object? rawValue)
    {
        if (rawValue is not JsonElement el) return false;
        // True / false set the flag; explicit JSON null clears it.
        // Anything else (numbers, strings, undefined value) is a no-op
        // - we don't want to flap the dashed outline based on a
        // malformed delta.
        bool? newVal = el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => Virtual,    // unchanged sentinel
        };
        if (Virtual == newVal) return false;
        Virtual = newVal;
        return true;
    }

    private static string? AsString(object? rawValue)
    {
        if (rawValue is null) return null;
        if (rawValue is JsonElement el && el.ValueKind == JsonValueKind.String)
            return el.GetString();
        return rawValue.ToString();
    }

    /// <summary>Extracts the 9-digit MMSI from a SignalK AtoN context
    /// of the form <c>atons.urn:mrn:imo:mmsi:NNNNNNNNN</c>. Returns null
    /// when the context doesn't follow the canonical shape (e.g. some
    /// plugins use a different identifier scheme).</summary>
    public static string? ExtractMmsi(string context)
    {
        // Last colon-separated segment after "mmsi:" is the 9-digit id.
        const string marker = "mmsi:";
        var idx = context.LastIndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        var tail = context[(idx + marker.Length)..];
        return tail.Length > 0 ? tail : null;
    }
}
