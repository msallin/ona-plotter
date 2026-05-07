using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;

namespace OnaPlotter.Utilities;

/// <summary>
/// Parses a single GPX 1.1 or GeoJSON RFC 7946 payload into typed
/// route / waypoint / note / region records the Resources page can
/// hand to the existing <c>RouteApi</c> / <c>WaypointApi</c> /
/// <c>NoteApi</c> / <c>RegionApi</c> create methods.
/// <para>
/// Mirrors <see cref="ResourceExporter"/>'s shape conventions on
/// the read side: GeoJSON Point + a <c>title</c> property reads as
/// a Note (round-trips with the exporter's NoteGeoJson); Point
/// without title reads as a Waypoint; LineString reads as a Route;
/// Polygon / MultiPolygon reads as a Region. GPX <c>rte</c> reads
/// as a Route; GPX <c>wpt</c> reads as a Waypoint (no native
/// note / region shape in GPX 1.1).
/// </para>
/// <para>
/// Format detection is content-sniffing: first non-whitespace
/// character. <c>&lt;</c> -&gt; XML / GPX; <c>{</c> or <c>[</c> -&gt;
/// JSON / GeoJSON. Filename extension is not consulted - helms
/// drop in files from various sources and the extension is
/// sometimes wrong (e.g. a "<c>passage.txt</c>" that contains
/// GPX). Sniffing makes the import robust to that.
/// </para>
/// </summary>
public static class ResourceImporter
{
    private static readonly XNamespace Gpx = "http://www.topografix.com/GPX/1/1";

    /// <summary>Sniffs the first non-whitespace byte to decide
    /// GPX vs GeoJSON. Returns null when the payload is neither
    /// (binary file, malformed, empty).</summary>
    public static ImportFormat? DetectFormat(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        // First non-whitespace char.
        for (int i = 0; i < content.Length; i++)
        {
            char c = content[i];
            if (char.IsWhiteSpace(c)) continue;
            return c switch
            {
                '<' => ImportFormat.Gpx,
                '{' or '[' => ImportFormat.GeoJson,
                _ => (ImportFormat?)null,
            };
        }
        return null;
    }

    /// <summary>Parse a GPX or GeoJSON payload, auto-detecting
    /// which one. Throws on malformed XML / JSON; the caller
    /// (Resources.razor) wraps with a user-facing toast.</summary>
    public static ImportResult Parse(string content)
    {
        var fmt = DetectFormat(content)
            ?? throw new FormatException(
                "Unrecognised import format. Expected GPX (XML) or GeoJSON (JSON).");
        return fmt switch
        {
            ImportFormat.Gpx => ParseGpx(content),
            ImportFormat.GeoJson => ParseGeoJson(content),
            _ => throw new FormatException("Unsupported import format."),
        };
    }

    // ---------------- GPX -----------------------------------------

    public static ImportResult ParseGpx(string xml)
    {
        var doc = XDocument.Parse(xml);
        var root = doc.Root
            ?? throw new FormatException("GPX document has no root element.");

        var routes = new List<ImportedRoute>();
        var waypoints = new List<ImportedWaypoint>();
        var inv = CultureInfo.InvariantCulture;

        foreach (var rte in root.Descendants(Gpx + "rte"))
        {
            var name = (string?)rte.Element(Gpx + "name") ?? "Imported route";
            var description = (string?)rte.Element(Gpx + "desc");
            var coords = new List<double[]>();
            foreach (var rtept in rte.Elements(Gpx + "rtept"))
            {
                if (TryReadLatLon(rtept, out var lat, out var lon))
                    coords.Add([lat, lon]);
            }
            // < 2 points -> not a meaningful route. Skip silently;
            // GPX writers occasionally emit empty rte elements as
            // header/metadata stubs.
            if (coords.Count >= 2)
                routes.Add(new ImportedRoute(name, description, [.. coords]));
        }

        foreach (var wpt in root.Descendants(Gpx + "wpt"))
        {
            if (!TryReadLatLon(wpt, out var lat, out var lon)) continue;
            var name = (string?)wpt.Element(Gpx + "name") ?? "Imported waypoint";
            var description = (string?)wpt.Element(Gpx + "desc");
            waypoints.Add(new ImportedWaypoint(name, description, lat, lon));
        }

        return new ImportResult(routes, waypoints, [], []);

        bool TryReadLatLon(XElement el, out double lat, out double lon)
        {
            lat = 0; lon = 0;
            return double.TryParse((string?)el.Attribute("lat"),
                       NumberStyles.Float, inv, out lat)
                && double.TryParse((string?)el.Attribute("lon"),
                       NumberStyles.Float, inv, out lon);
        }
    }

    // ---------------- GeoJSON -------------------------------------

    public static ImportResult ParseGeoJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var routes = new List<ImportedRoute>();
        var waypoints = new List<ImportedWaypoint>();
        var notes = new List<ImportedNote>();
        var regions = new List<ImportedRegion>();

        var root = doc.RootElement;
        // Accept both:
        //   { type: "FeatureCollection", features: [ ... ] }
        //   { type: "Feature", ... }     <- single feature
        //   [ ... ]                      <- bare array of features
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("type", out var tEl)
            && tEl.GetString() == "FeatureCollection"
            && root.TryGetProperty("features", out var feats))
        {
            foreach (var f in feats.EnumerateArray())
                Dispatch(f, routes, waypoints, notes, regions);
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            Dispatch(root, routes, waypoints, notes, regions);
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in root.EnumerateArray())
                Dispatch(f, routes, waypoints, notes, regions);
        }
        else
        {
            throw new FormatException(
                "GeoJSON root must be a Feature, FeatureCollection, or array of Features.");
        }

        return new ImportResult(routes, waypoints, notes, regions);
    }

    private static void Dispatch(
        JsonElement feature,
        List<ImportedRoute> routes,
        List<ImportedWaypoint> waypoints,
        List<ImportedNote> notes,
        List<ImportedRegion> regions)
    {
        if (feature.ValueKind != JsonValueKind.Object) return;
        if (!feature.TryGetProperty("geometry", out var geom)
            || geom.ValueKind != JsonValueKind.Object) return;
        if (!geom.TryGetProperty("type", out var typeEl)) return;
        var type = typeEl.GetString();
        if (type is null) return;

        var props = feature.TryGetProperty("properties", out var p)
            && p.ValueKind == JsonValueKind.Object ? p : default;
        var coords = geom.TryGetProperty("coordinates", out var c) ? c : default;

        switch (type)
        {
            case "LineString":
                {
                    var ring = ReadRing(coords);
                    if (ring.Length < 2) return;
                    var name = ReadString(props, "name") ?? "Imported route";
                    var description = ReadString(props, "description");
                    routes.Add(new ImportedRoute(name, description, ring));
                    return;
                }
            case "Point":
                {
                    if (coords.ValueKind != JsonValueKind.Array
                        || coords.GetArrayLength() < 2) return;
                    double lon = coords[0].GetDouble();
                    double lat = coords[1].GetDouble();
                    // Title-only -> Note (matches ResourceExporter.NoteGeoJson).
                    // Name-only -> Waypoint. Both -> prefer Note (the more
                    // specific shape; SignalK Notes carry both fields).
                    var title = ReadString(props, "title");
                    var name = ReadString(props, "name");
                    var description = ReadString(props, "description");
                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        notes.Add(new ImportedNote(title!, description, lat, lon));
                    }
                    else
                    {
                        waypoints.Add(new ImportedWaypoint(
                            name ?? "Imported waypoint", description, lat, lon));
                    }
                    return;
                }
            case "Polygon":
                {
                    // GeoJSON Polygon coordinates = [[outer ring], [hole], ...].
                    // Use the outer ring; ignore holes (matches
                    // ResourceExporter, which also drops holes).
                    if (coords.ValueKind != JsonValueKind.Array
                        || coords.GetArrayLength() < 1) return;
                    var ring = ReadRing(coords[0]);
                    if (ring.Length < 3) return;
                    var name = ReadString(props, "name") ?? "Imported region";
                    var description = ReadString(props, "description");
                    regions.Add(new ImportedRegion(name, description, ring));
                    return;
                }
            case "MultiPolygon":
                {
                    // [[[outer], [hole]], [[outer], ...], ...]
                    if (coords.ValueKind != JsonValueKind.Array) return;
                    var name = ReadString(props, "name") ?? "Imported region";
                    var description = ReadString(props, "description");
                    int idx = 0;
                    foreach (var poly in coords.EnumerateArray())
                    {
                        if (poly.ValueKind != JsonValueKind.Array
                            || poly.GetArrayLength() < 1) continue;
                        var ring = ReadRing(poly[0]);
                        if (ring.Length < 3) continue;
                        // Suffix multi-polygon shards "<name> 2", "<name> 3"
                        // so the helm sees them as distinct rows in the
                        // Resources list. Single Polygon -> no suffix.
                        idx++;
                        var rname = idx == 1 ? name : $"{name} {idx}";
                        regions.Add(new ImportedRegion(rname, description, ring));
                    }
                    return;
                }
            default:
                // Unknown geometry type (e.g. GeometryCollection,
                // MultiLineString). Skip silently rather than fail
                // the whole import for one stray feature.
                return;
        }
    }

    /// <summary>Read a GeoJSON LineString or polygon-ring coordinate
    /// array into a Leaflet-order [lat, lon] double[][].</summary>
    private static double[][] ReadRing(JsonElement coords)
    {
        if (coords.ValueKind != JsonValueKind.Array) return [];
        var pts = new List<double[]>(coords.GetArrayLength());
        foreach (var p in coords.EnumerateArray())
        {
            if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() < 2) continue;
            // GeoJSON [lon, lat] -> Leaflet [lat, lon].
            pts.Add([p[1].GetDouble(), p[0].GetDouble()]);
        }
        return [.. pts];
    }

    private static string? ReadString(JsonElement props, string key)
    {
        if (props.ValueKind != JsonValueKind.Object) return null;
        if (!props.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }
}

public enum ImportFormat { Gpx, GeoJson }

public sealed record ImportedRoute(string Name, string? Description, double[][] CoordsLatLon);
public sealed record ImportedWaypoint(string Name, string? Description, double Lat, double Lon);
public sealed record ImportedNote(string Title, string? Description, double Lat, double Lon);
public sealed record ImportedRegion(string Name, string? Description, double[][] RingLatLon);

public sealed record ImportResult(
    IReadOnlyList<ImportedRoute> Routes,
    IReadOnlyList<ImportedWaypoint> Waypoints,
    IReadOnlyList<ImportedNote> Notes,
    IReadOnlyList<ImportedRegion> Regions)
{
    public int TotalCount => Routes.Count + Waypoints.Count + Notes.Count + Regions.Count;
}
