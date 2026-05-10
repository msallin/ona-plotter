using System.Text.Json;
using OnaPlotter.Models;

namespace OnaPlotter.Services.Resources;

/// <summary>
/// Content-equality helpers for the four <c>SignalK</c> resource DTOs
/// (route, waypoint, note, region). Used by <see cref="ResourceTypeCache{T}"/>
/// to suppress <c>Changed</c> events when an upserted entry is byte-
/// identical to the cached one.
///
/// <para><b>Why this exists:</b> on a WS reconnect-edge reconcile,
/// <see cref="ResourceTypeCache{T}.Replace"/> walks every entry the
/// server returned and fires <c>Changed</c> for each, regardless of
/// whether the content actually differs from the cached copy. On a
/// fleet-cruising boat with 90+ resources per type that produced 90+
/// fire-and-forget tasks racing through the same JS-interop bridge,
/// blowing the WASM 5 MB stack with "memory access out of bounds".
/// Source-side dedup collapses that to "fire only for entries that
/// genuinely changed during the disconnect window" - normally zero on
/// a stable cruise.</para>
///
/// <para><b>Comparison strategy:</b> field-by-field, falling back to
/// <see cref="JsonElement.GetRawText"/> for the GeoJSON
/// <c>coordinates</c> blob. Reflection-based <see cref="JsonSerializer"/>
/// would be simpler but the project's <c>TrimMode=partial</c> stance
/// requires every <c>JsonSerializer</c> call to route through a
/// source-generated context (see <c>OnaJsonContext</c>). Adding the
/// four resource types just for dedup would inflate the bundle; the
/// hand-rolled comparers are ~5 ms per reconcile (measured on Pi 4)
/// for the worst case of 100+ entries.</para>
///
/// <para><b>Future-proofing:</b> when a new field is added to any of
/// these DTOs, add a clause here too. A missing clause means the
/// dedup falsely reports "unchanged" and the UI silently ignores the
/// new field's update on the reconnect-replay path. The delta path
/// (<see cref="ResourceTypeCache{T}.Apply"/>) hits the same dedup, so
/// a missing clause also masks delta updates - same risk.</para>
/// </summary>
internal static class ResourceContentEquality
{
    public static bool RouteEquals(SignalkRoute a, SignalkRoute b)
    {
        if (ReferenceEquals(a, b)) return true;
        return a.Id == b.Id
            && a.Name == b.Name
            && a.Description == b.Description
            && a.Distance == b.Distance
            && FeatureEquals(a.Feature, b.Feature);
    }

    public static bool WaypointEquals(SignalkWaypoint a, SignalkWaypoint b)
    {
        if (ReferenceEquals(a, b)) return true;
        return a.Id == b.Id
            && a.Name == b.Name
            && a.Description == b.Description
            && a.Latitude == b.Latitude
            && a.Longitude == b.Longitude
            && a.CreatedAt == b.CreatedAt
            && a.IsMob == b.IsMob
            && a.IsMobActive == b.IsMobActive
            && a.MobAlarmId == b.MobAlarmId
            && FeatureEquals(a.Feature, b.Feature);
    }

    public static bool NoteEquals(SignalkNote a, SignalkNote b)
    {
        if (ReferenceEquals(a, b)) return true;
        return a.Id == b.Id
            && a.Title == b.Title
            && a.Description == b.Description
            && a.MimeType == b.MimeType
            && a.Url == b.Url
            && a.CreatedAt == b.CreatedAt
            && PositionEquals(a.Position, b.Position);
    }

    public static bool RegionEquals(SignalkRegion a, SignalkRegion b)
    {
        if (ReferenceEquals(a, b)) return true;
        return a.Id == b.Id
            && a.Name == b.Name
            && a.Description == b.Description
            && a.IsHazard == b.IsHazard
            && a.CreatedAt == b.CreatedAt
            && a.CenterLat == b.CenterLat
            && a.CenterLon == b.CenterLon
            && a.RadiusMeters == b.RadiusMeters
            && FeatureEquals(a.Feature, b.Feature)
            && OuterRingsEqual(a.OuterRings, b.OuterRings);
    }

    private static bool FeatureEquals(GeoJsonFeature? a, GeoJsonFeature? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        return a.Type == b.Type
            && GeometryEquals(a.Geometry, b.Geometry)
            && PropertiesEquals(a.Properties, b.Properties);
    }

    private static bool GeometryEquals(GeoJsonGeometry? a, GeoJsonGeometry? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (a.Type != b.Type) return false;
        return JsonElementContentEquals(a.Coordinates, b.Coordinates);
    }

    private static bool PropertiesEquals(GeoJsonProperties? a, GeoJsonProperties? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        return a.Name == b.Name
            && a.Description == b.Description
            && a.IsHazard == b.IsHazard
            && a.IsMob == b.IsMob
            && a.IsMobActive == b.IsMobActive
            && a.MobAlarmId == b.MobAlarmId;
    }

    private static bool PositionEquals(NotePosition? a, NotePosition? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        return a.Latitude == b.Latitude && a.Longitude == b.Longitude;
    }

    /// <summary>Compares two <see cref="JsonElement"/> values by their
    /// raw token text. <see cref="JsonElement.GetRawText"/> returns the
    /// substring of the underlying buffer that produced the element,
    /// which is the cheapest available semantically-correct comparison
    /// (no re-serialisation, just a string allocation). For two
    /// elements parsed from the same server response of identical
    /// content, the raw text is byte-identical.</summary>
    private static bool JsonElementContentEquals(JsonElement a, JsonElement b)
    {
        // Default JsonElement (ValueKind.Undefined): treat as equal so
        // a route document missing the geometry block doesn't bounce
        // through Changed every reconcile.
        if (a.ValueKind == JsonValueKind.Undefined && b.ValueKind == JsonValueKind.Undefined)
            return true;
        if (a.ValueKind != b.ValueKind) return false;
        return string.Equals(a.GetRawText(), b.GetRawText(), StringComparison.Ordinal);
    }

    private static bool OuterRingsEqual(
        IReadOnlyList<double[][]> a,
        IReadOnlyList<double[][]> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            var ra = a[i];
            var rb = b[i];
            if (ra.Length != rb.Length) return false;
            for (int j = 0; j < ra.Length; j++)
            {
                var pa = ra[j];
                var pb = rb[j];
                if (pa.Length != pb.Length) return false;
                for (int k = 0; k < pa.Length; k++)
                {
                    // Bitwise compare via != is correct for finite
                    // doubles; NaN coordinates aren't a thing in this
                    // codebase (parsed from JSON numbers, not user
                    // arithmetic) so the NaN != NaN edge case doesn't
                    // apply.
                    if (pa[k] != pb[k]) return false;
                }
            }
        }
        return true;
    }
}
