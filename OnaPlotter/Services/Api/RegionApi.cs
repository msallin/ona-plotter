using System.Net.Http.Json;
using System.Text.Json;
using OnaPlotter.Models;

namespace OnaPlotter.Services.Api;

/// <summary>
/// SignalK regions client. Regions use a GeoJSON Feature wrapper with
/// Polygon or MultiPolygon geometry, same family as routes/waypoints.
/// Circles are emitted as 32-vertex polygon approximations to stay
/// wire-compatible with Freeboard-SK and any future consumer that only
/// understands GeoJSON Polygons.
/// </summary>
public sealed class RegionApi : IRegionApi
{
    /// <summary>Approximation resolution for circle-creation. 32 is
    /// visually indistinguishable from a true circle at chart zooms up
    /// to ~100m/px and is 64 floats on the wire, trivially cheap.</summary>
    private const int CircleVertexCount = 32;

    /// <summary>Average metres per degree of latitude anywhere on the
    /// sphere. Close enough for circle approximation at the few-km
    /// scales we care about; we're drawing a circle, not navigating
    /// by dead reckoning.</summary>
    private const double MetersPerDegLatitude = 111_320.0;

    private readonly HttpClient _http;
    private readonly ISignalKBaseUrl _baseUrl;

    public RegionApi(HttpClient http, ISignalKBaseUrl baseUrl)
    {
        _http = http;
        _baseUrl = baseUrl;
    }

    public async Task<List<SignalkRegion>> GetAllAsync(CancellationToken ct = default)
    {
        var dict = await ResourceHttp.GetDictAsync<SignalkRegion>(
            _http, _baseUrl.Combine(SignalKUrls.RegionsPath), ct);
        if (dict is null) return [];

        var regions = new List<SignalkRegion>(dict.Count);
        foreach (var (key, region) in dict)
        {
            region.Id = key;
            var coords = region.Feature?.Geometry?.Coordinates;
            var type = region.Feature?.Geometry?.Type;
            if (coords is not null)
                region.OuterRings = ExtractOuterRings(coords.Value, type);

            // Drop regions with no renderable geometry so the map layer
            // doesn't have to special-case an empty rings list.
            if (region.OuterRings.Count == 0) continue;
            regions.Add(region);
        }
        return regions;
    }

    public Task<string?> CreateCircleAsync(string name, string description,
        double lat, double lon, double radiusMeters, CancellationToken ct = default)
    {
        var ring = BuildCircleRing(lat, lon, radiusMeters, CircleVertexCount);
        return PostPolygonAsync(name, description, ring, ct);
    }

    public Task<string?> CreatePolygonAsync(string name, string description,
        double[][] vertices, CancellationToken ct = default)
    {
        // Freeform polygon: map the [lat, lon] Leaflet vertices back to
        // GeoJSON [lon, lat] order and close the ring by repeating the
        // first vertex at the end.
        if (vertices is null || vertices.Length < 3) return Task.FromResult<string?>(null);
        var ring = new double[vertices.Length + 1][];
        for (int i = 0; i < vertices.Length; i++)
        {
            if (vertices[i] is null || vertices[i].Length < 2) return Task.FromResult<string?>(null);
            ring[i] = [vertices[i][1], vertices[i][0]];
        }
        ring[vertices.Length] = ring[0];
        return PostPolygonAsync(name, description, ring, ct);
    }

    /// <summary>Posts the common body shape for both circle-derived and
    /// freeform polygons. Factored out so the two CreateXxxAsync methods
    /// differ only in how they build their ring.</summary>
    private async Task<string?> PostPolygonAsync(string name, string description,
        double[][] ring, CancellationToken ct)
    {
        // GeoJSON Polygon coordinates: array of linear rings; index 0 is
        // the outer ring, remaining indices are holes (we have none).
        var coordinates = new[] { ring };

        var body = new
        {
            name,
            description,
            feature = new
            {
                type = "Feature",
                geometry = new { type = "Polygon", coordinates },
                properties = new
                {
                    name,
                    description,
                }
            }
        };
        var url = _baseUrl.Combine(SignalKUrls.RegionsPath);
        using var response = await _http.PostAsJsonAsync(url, body, ct);
        if (!response.IsSuccessStatusCode) return null;
        var result = await response.Content.ReadAsStringAsync(ct);
        return ResourceHttp.ParseCreatedId(result);
    }

    public Task<bool> DeleteAsync(string id, CancellationToken ct = default) =>
        ResourceHttp.DeleteAsync(_http, _baseUrl.Combine(SignalKUrls.Region(id)), ct);

    /// <summary>
    /// Builds a closed linear ring approximating a circle of the given
    /// radius (metres) about a lat/lon centre. Output is GeoJSON order:
    /// each vertex is <c>[lon, lat]</c>, and the first vertex is
    /// repeated at the end to close the ring.
    /// </summary>
    internal static double[][] BuildCircleRing(double lat, double lon, double radiusMeters, int vertices)
    {
        double cosLat = Math.Cos(lat * Math.PI / 180.0);
        double metersPerDegLon = MetersPerDegLatitude * cosLat;
        if (metersPerDegLon < 1) metersPerDegLon = 1; // pole safety

        var ring = new double[vertices + 1][];
        for (int i = 0; i < vertices; i++)
        {
            double angle = 2 * Math.PI * i / vertices;
            double dLat = (radiusMeters * Math.Cos(angle)) / MetersPerDegLatitude;
            double dLon = (radiusMeters * Math.Sin(angle)) / metersPerDegLon;
            ring[i] = [lon + dLon, lat + dLat];
        }
        ring[vertices] = ring[0]; // close
        return ring;
    }

    /// <summary>
    /// Extracts every outer ring from a GeoJSON Polygon or MultiPolygon
    /// <c>coordinates</c> element. Output rings are in Leaflet-native
    /// <c>[lat, lon]</c> order and do NOT include holes. Returns an
    /// empty list for geometry types we don't understand.
    /// </summary>
    internal static List<double[][]> ExtractOuterRings(JsonElement coords, string? geometryType)
    {
        var result = new List<double[][]>();
        if (coords.ValueKind != JsonValueKind.Array) return result;

        switch (geometryType)
        {
            case "Polygon":
                if (coords.GetArrayLength() > 0)
                    result.Add(ParseRing(coords[0]));
                break;
            case "MultiPolygon":
                foreach (var polygon in coords.EnumerateArray())
                {
                    if (polygon.ValueKind == JsonValueKind.Array && polygon.GetArrayLength() > 0)
                        result.Add(ParseRing(polygon[0]));
                }
                break;
        }
        // Drop empty rings from the result so callers don't see
        // degenerate single-vertex "polygons".
        result.RemoveAll(r => r.Length < 3);
        return result;
    }

    private static double[][] ParseRing(JsonElement ring)
    {
        if (ring.ValueKind != JsonValueKind.Array) return [];
        var list = new List<double[]>(ring.GetArrayLength());
        foreach (var coord in ring.EnumerateArray())
        {
            if (coord.ValueKind != JsonValueKind.Array || coord.GetArrayLength() < 2) continue;
            double lon = coord[0].GetDouble();
            double lat = coord[1].GetDouble();
            list.Add([lat, lon]); // swap to Leaflet order
        }
        return [.. list];
    }
}
