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
            // Lift description + MOB metadata onto the flat
            // SignalkWaypoint projection fields. Helper lives on the
            // model so the WS-delta path in ResourceStore stays in
            // lockstep with this REST path.
            wp.LiftFromFeatureProperties();
            waypoints.Add(wp);
        }
        return waypoints;
    }

    public Task<ApiResult<string>> CreateAsync(string name, double lat, double lon,
        string? description = null, CancellationToken ct = default)
        => CreateAsync(name, lat, lon, description, null, null, null, ct);

    /// <summary>Create overload that also stamps the MOB metadata
    /// fields (<c>isMob</c> / <c>isActive</c> / <c>mobAlarmId</c>)
    /// when present. Call from <c>MobService.RaiseAsync</c> so the
    /// resulting waypoint renders with the pulsing MOB icon and
    /// correlates with the SignalK notification id. Non-MOB
    /// callers use the simpler 4-arg overload above.</summary>
    public Task<ApiResult<string>> CreateAsync(string name, double lat, double lon,
        string? description, bool? isMob, bool? isActive, string? mobAlarmId,
        CancellationToken ct = default)
    {
        // Shared GeoJSON envelope builder: SignalK core + peer
        // clients refuse feature documents without a `properties`
        // block, so the helper always emits one (empty description
        // is the spec-friendly "no description" value, not a missing
        // key).
        // CreatedAt is stamped on first PUT so the popup can show
        // "when was this pinned" without a sidecar resource. Same
        // approach as SignalkNote - relies on resources-fs round-
        // tripping arbitrary top-level body fields.
        var body = GeoJsonBuilder.FeatureBody(
            name, GeoJsonBuilder.Point(lat, lon), description,
            createdAt: DateTime.UtcNow,
            isMob: isMob, isActive: isActive, mobAlarmId: mobAlarmId);
        var url = _baseUrl.Combine(SignalKUrls.WaypointsPath);
        return ResourceHttp.PostCreateAsync(_http, url, body, ct);
    }

    public Task<ApiResult> DeleteAsync(string id, CancellationToken ct = default) =>
        ResourceHttp.DeleteAsync(_http, _baseUrl.Combine(SignalKUrls.Waypoint(id)), ct);

    public Task<ApiResult> UpdateAsync(SignalkWaypoint wp, string name, string? description = null, CancellationToken ct = default)
        => UpdateAsync(wp, name, description, null, null, null, ct);

    /// <summary>Update overload that also writes MOB metadata. Pass
    /// the existing waypoint's flags through verbatim when editing
    /// a non-MOB waypoint (the simpler overload above does this);
    /// pass <c>isActive: false</c> from <c>MobService.ClearAsync</c>
    /// to deactivate without deleting.</summary>
    public Task<ApiResult> UpdateAsync(SignalkWaypoint wp, string name, string? description,
        bool? isMob, bool? isActive, string? mobAlarmId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(wp.Id)) return Task.FromResult(ApiResult.Fail("waypoint id required"));
        if (wp.Latitude is not double lat || wp.Longitude is not double lon)
            return Task.FromResult(ApiResult.Fail("waypoint position required"));
        // Same envelope as Create; PUT at the waypoint's id URL is
        // the v2 resources-api in-place update verb. CreatedAt
        // round-trips from the existing waypoint so an Edit doesn't
        // reset the "first pinned" timestamp.
        var body = GeoJsonBuilder.FeatureBody(
            name, GeoJsonBuilder.Point(lat, lon), description,
            createdAt: wp.CreatedAt,
            isMob: isMob, isActive: isActive, mobAlarmId: mobAlarmId);
        var url = _baseUrl.Combine(SignalKUrls.Waypoint(wp.Id));
        return ResourceHttp.PutAsync(_http, url, body, ct);
    }

    public Task<ApiResult> PutWithIdAsync(string id, string name, double lat, double lon,
        string? description, bool? isMob, bool? isActive, string? mobAlarmId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(id)) return Task.FromResult(ApiResult.Fail("waypoint id required"));
        // Same body builder as Create / Update; SK v2 resources-api
        // accepts PUT on a not-yet-existing id and creates the
        // resource at that id (idempotent: a retry just overwrites).
        // CreatedAt is stamped here (rather than carried from an
        // existing instance) because the caller is the helm raising
        // a fresh MOB - there's no prior server-side resource to
        // preserve a timestamp from.
        var body = GeoJsonBuilder.FeatureBody(
            name, GeoJsonBuilder.Point(lat, lon), description,
            createdAt: DateTime.UtcNow,
            isMob: isMob, isActive: isActive, mobAlarmId: mobAlarmId);
        var url = _baseUrl.Combine(SignalKUrls.Waypoint(id));
        return ResourceHttp.PutAsync(_http, url, body, ct);
    }
}
