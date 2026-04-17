using System.Text.Json;

namespace OnaPlotter.Utilities;

/// <summary>
/// Helpers for parsing SignalK and GeoJSON <see cref="JsonElement"/> payloads
/// without adding typed-DTO overhead for variably-nested structures.
/// </summary>
public static class JsonElementExtensions
{
    /// <summary>
    /// Recursively walks an object tree and returns dotted paths to every
    /// leaf that has a 'value' property (SignalK convention for data paths).
    /// Example: { navigation: { position: { value: {...} } } } yields "navigation.position".
    /// </summary>
    public static List<string> FlattenSignalKPaths(this JsonElement root)
    {
        var paths = new List<string>();
        if (root.ValueKind == JsonValueKind.Object)
            Walk(root, "", paths);
        paths.Sort(StringComparer.OrdinalIgnoreCase);
        return paths;

        static void Walk(JsonElement element, string prefix, List<string> paths)
        {
            foreach (var prop in element.EnumerateObject())
            {
                var path = prefix.Length == 0 ? prop.Name : $"{prefix}.{prop.Name}";
                if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    if (prop.Value.TryGetProperty("value", out _))
                        paths.Add(path);
                    else
                        Walk(prop.Value, path, paths);
                }
            }
        }
    }

    /// <summary>
    /// Parses a GeoJSON LineString 'coordinates' array ([[lon,lat], ...])
    /// and returns Leaflet-ordered [lat, lon] pairs.
    /// </summary>
    public static double[][] ToLeafletLineString(this JsonElement coordinates)
    {
        if (coordinates.ValueKind != JsonValueKind.Array) return [];
        var result = new List<double[]>(coordinates.GetArrayLength());
        foreach (var point in coordinates.EnumerateArray())
        {
            if (TryReadLonLat(point, out double lon, out double lat))
                result.Add([lat, lon]);
        }
        return [.. result];
    }

    /// <summary>
    /// Parses a GeoJSON Point 'coordinates' array ([lon, lat]).
    /// Returns (lat, lon) or null if the array is malformed.
    /// </summary>
    public static (double Latitude, double Longitude)? ToLatLonPoint(this JsonElement coordinates)
    {
        if (TryReadLonLat(coordinates, out double lon, out double lat))
            return (lat, lon);
        return null;
    }

    /// <summary>
    /// Computes an axis-aligned bounding box (min/max lat/lon) from a GeoJSON
    /// LineString 'coordinates' array. Returns null for empty or malformed input.
    /// </summary>
    public static (double MinLat, double MaxLat, double MinLon, double MaxLon)? LineStringBounds(this JsonElement coordinates)
    {
        if (coordinates.ValueKind != JsonValueKind.Array) return null;

        double minLat = double.MaxValue, maxLat = double.MinValue;
        double minLon = double.MaxValue, maxLon = double.MinValue;
        bool any = false;

        foreach (var point in coordinates.EnumerateArray())
        {
            if (!TryReadLonLat(point, out double lon, out double lat)) continue;
            if (lat < minLat) minLat = lat;
            if (lat > maxLat) maxLat = lat;
            if (lon < minLon) minLon = lon;
            if (lon > maxLon) maxLon = lon;
            any = true;
        }

        return any ? (minLat, maxLat, minLon, maxLon) : null;
    }

    private static bool TryReadLonLat(JsonElement point, out double lon, out double lat)
    {
        lon = 0; lat = 0;
        if (point.ValueKind != JsonValueKind.Array) return false;

        int i = 0;
        foreach (var val in point.EnumerateArray())
        {
            if (val.ValueKind != JsonValueKind.Number) return false;
            if (i == 0) lon = val.GetDouble();
            else if (i == 1) { lat = val.GetDouble(); return true; }
            i++;
        }
        return false;
    }
}
