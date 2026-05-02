using OnaPlotter.Models;
using OnaPlotter.Utilities;

namespace OnaPlotter.Services.Api;

public sealed class WaypointApi : IWaypointApi
{
    private readonly HttpClient _http;
    private readonly ISignalKBaseUrl _baseUrl;

    public WaypointApi(HttpClient http, ISignalKBaseUrl baseUrl)
    {
        _http = http;
        _baseUrl = baseUrl;
    }

    public async Task<List<SignalkWaypoint>> GetAllAsync(CancellationToken ct = default)
    {
        var dict = await ResourceHttp.GetDictAsync<SignalkWaypoint>(
            _http, _baseUrl.Combine(SignalKUrls.WaypointsPath), ct);
        if (dict is null) return [];

        var waypoints = new List<SignalkWaypoint>(dict.Count);
        foreach (var (key, wp) in dict)
        {
            wp.Id = key;
            var coords = wp.Feature?.Geometry?.Coordinates;
            if (coords is not null)
            {
                var latLon = coords.Value.ToLatLonPoint();
                if (latLon is not null)
                {
                    wp.Latitude = latLon.Value.Latitude;
                    wp.Longitude = latLon.Value.Longitude;
                }
            }
            // Lift the description out of the GeoJSON properties block
            // into a flat field so the popup-Edit dialog can pre-fill
            // it. Empty string is the absent-description value
            // GeoJsonBuilder.FeatureBody emits on Create; treat it as
            // null so the textarea placeholder ("Description (optional)")
            // surfaces instead of an empty input.
            var desc = wp.Feature?.Properties?.Description;
            wp.Description = string.IsNullOrEmpty(desc) ? null : desc;
            waypoints.Add(wp);
        }
        return waypoints;
    }

    public Task<ApiResult<string>> CreateAsync(string name, double lat, double lon,
        string? description = null, CancellationToken ct = default)
    {
        // Shared GeoJSON envelope builder: Freeboard-SK + SK core
        // refuse feature documents without a `properties` block, so
        // the helper always emits one (empty description is the
        // spec-friendly "no description" value, not a missing key).
        // CreatedAt is stamped on first PUT so the popup can show
        // "when was this pinned" without a sidecar resource. Same
        // approach as SignalkNote -- relies on resources-fs round-
        // tripping arbitrary top-level body fields.
        var body = GeoJsonBuilder.FeatureBody(
            name, GeoJsonBuilder.Point(lat, lon), description, createdAt: DateTime.UtcNow);
        var url = _baseUrl.Combine(SignalKUrls.WaypointsPath);
        return ResourceHttp.PostCreateAsync(_http, url, body, ct);
    }

    public Task<ApiResult> DeleteAsync(string id, CancellationToken ct = default) =>
        ResourceHttp.DeleteAsync(_http, _baseUrl.Combine(SignalKUrls.Waypoint(id)), ct);

    public Task<ApiResult> UpdateAsync(SignalkWaypoint wp, string name, string? description = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(wp.Id)) return Task.FromResult(ApiResult.Fail("waypoint id required"));
        if (wp.Latitude is not double lat || wp.Longitude is not double lon)
            return Task.FromResult(ApiResult.Fail("waypoint position required"));
        // Same envelope as Create; PUT at the waypoint's id URL is
        // the v2 resources-api in-place update verb. CreatedAt
        // round-trips from the existing waypoint so an Edit doesn't
        // reset the "first pinned" timestamp.
        var body = GeoJsonBuilder.FeatureBody(
            name, GeoJsonBuilder.Point(lat, lon), description, createdAt: wp.CreatedAt);
        var url = _baseUrl.Combine(SignalKUrls.Waypoint(wp.Id));
        return ResourceHttp.PutAsync(_http, url, body, ct);
    }
}
