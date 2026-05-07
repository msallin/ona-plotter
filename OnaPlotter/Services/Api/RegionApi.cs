using System.Text.Json;
using OnaPlotter.Models;
using OnaPlotter.Utilities;

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

    public Task<ApiResult<string>> CreateCircleAsync(string name, string description,
        double lat, double lon, double radiusMeters,
        bool isHazard = false, CancellationToken ct = default)
    {
        var ring = CircleGeometry.BuildRing(lat, lon, radiusMeters, CircleGeometry.DefaultVertexCount);
        // Stamp createdAt + circle metadata so the popup can render
        // "Created" and "centre + radius" instead of a 32-vertex
        // polygon dump. The resources-fs provider round-trips
        // unknown top-level fields, so this is enough to preserve
        // the hint across re-fetches.
        var body = GeoJsonBuilder.RegionFeatureBody(
            name, GeoJsonBuilder.Polygon(ring), description, isHazard,
            createdAt: DateTime.UtcNow,
            centerLat: lat, centerLon: lon, radiusMeters: radiusMeters);
        return ResourceHttp.PostCreateAsync(
            _http, _baseUrl.Combine(SignalKUrls.RegionsPath), body, ct);
    }

    public Task<ApiResult<string>> CreatePolygonAsync(string name, string description,
        double[][] vertices,
        bool isHazard = false, CancellationToken ct = default)
    {
        // Freeform polygon: map the [lat, lon] Leaflet vertices back to
        // GeoJSON [lon, lat] order and close the ring by repeating the
        // first vertex at the end. Minimum 3 vertices - the toast on
        // fewer is more useful than a null return that looked identical
        // to a server-side rejection.
        var ring = BuildClosedRingFromLeaflet(vertices, out var err);
        if (ring is null) return Task.FromResult(ApiResult<string>.Fail(err!));
        var body = GeoJsonBuilder.RegionFeatureBody(
            name, GeoJsonBuilder.Polygon(ring), description, isHazard,
            createdAt: DateTime.UtcNow);
        return ResourceHttp.PostCreateAsync(
            _http, _baseUrl.Combine(SignalKUrls.RegionsPath), body, ct);
    }

    /// <summary>In-place update for polygon regions. PUT /resources/regions/{id}
    /// with a freshly-built polygon feature. Mirrors RouteApi.UpdateAsync -
    /// used when the user opens an existing region via the Layers-panel
    /// Edit button so tweaks replace the original instead of spawning
    /// a second region on save.
    /// <para>The resources-fs provider does FULL replacement on PUT, so the
    /// caller passes the original <paramref name="createdAt"/> + any
    /// circle metadata it has so those fields survive the round-trip.
    /// Pass null for both if the original region didn't carry them
    /// (legacy / imported regions); the UI falls back to a dash and
    /// the polygon-centroid coords accordingly.</para></summary>
    public Task<ApiResult> UpdatePolygonAsync(string id, string name, string description,
        double[][] vertices,
        bool isHazard = false,
        DateTime? createdAt = null,
        double? centerLat = null, double? centerLon = null, double? radiusMeters = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(id))
            return Task.FromResult(ApiResult.Fail("region id required"));
        var ring = BuildClosedRingFromLeaflet(vertices, out var err);
        if (ring is null) return Task.FromResult(ApiResult.Fail(err!));
        var body = GeoJsonBuilder.RegionFeatureBody(
            name,
            GeoJsonBuilder.Polygon(ring),
            description,
            isHazard,
            createdAt: createdAt,
            centerLat: centerLat, centerLon: centerLon, radiusMeters: radiusMeters);
        var url = _baseUrl.Combine(SignalKUrls.Region(id));
        return ResourceHttp.PutAsync(_http, url, body, ct);
    }

    /// <summary>Validates a Leaflet-order vertex array and returns a
    /// closed GeoJSON-order ring (last vertex == first), or null + an
    /// error message via <paramref name="err"/>. Factored out so
    /// CreatePolygonAsync and UpdatePolygonAsync share the validation
    /// + flip + close logic instead of duplicating the same eight
    /// lines twice.</summary>
    internal static double[][]? BuildClosedRingFromLeaflet(double[][]? vertices, out string? err)
    {
        if (vertices is null || vertices.Length < 3)
        {
            err = "polygon needs at least 3 vertices";
            return null;
        }
        var ring = new double[vertices.Length + 1][];
        for (int i = 0; i < vertices.Length; i++)
        {
            if (vertices[i] is null || vertices[i].Length < 2)
            {
                err = "polygon vertex missing lat / lon";
                return null;
            }
            ring[i] = [vertices[i][1], vertices[i][0]];
        }
        ring[vertices.Length] = ring[0];
        err = null;
        return ring;
    }

    public Task<ApiResult> DeleteAsync(string id, CancellationToken ct = default) =>
        ResourceHttp.DeleteAsync(_http, _baseUrl.Combine(SignalKUrls.Region(id)), ct);

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
