namespace OnaPlotter.Services.Api;

/// <summary>Default <see cref="IAnchorAlarmApi"/> hitting the v2.0.0+
/// SK PUT handlers + the one plugin-specific auto-radius POST. See
/// the interface docstring for the two-step flow rationale.</summary>
public sealed class AnchorAlarmApi : IAnchorAlarmApi
{
    private readonly HttpClient _http;
    private readonly ISignalKBaseUrl _baseUrl;

    public AnchorAlarmApi(HttpClient http, ISignalKBaseUrl baseUrl)
    {
        _http = http;
        _baseUrl = baseUrl;
    }

    public Task<ApiResult> DropAtCurrentPositionAsync(
        double latitude, double longitude, double? depthMeters,
        CancellationToken ct = default)
    {
        // Trust-boundary filter. A malformed GPS source can publish NaN
        // or +/-Infinity; System.Text.Json default options reject those
        // and throw inside PutAsJsonAsync. Even the catch-all wrapper
        // would surface the throw as "Specified value cannot be NaN" --
        // unintelligible to a helm. Refuse the call here with a
        // helm-readable error instead.
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude))
        {
            return Task.FromResult(ApiResult.Fail(
                "GPS fix is invalid (NaN / Infinity) -- can't drop anchor"));
        }
        // Depth is optional but if present must be finite. NaN depth
        // is treated as "no depth source" (omit altitude) rather than
        // as a hard error -- the helm can still anchor without a
        // depth sensor.
        double? safeDepth = (depthMeters is double d && double.IsFinite(d)) ? d : null;
        // SK convention for the position object: { latitude, longitude,
        // altitude } where altitude is metres ABOVE the SK datum.
        // Anchors sit on the seabed -- altitude = -|depth|. When the
        // helm has no depth source we omit altitude entirely; the v2
        // plugin tolerates the missing field and derives one from the
        // depth path itself when one is published. Mixed anonymous
        // types are stored as `object` so System.Text.Json picks them
        // up via runtime type discovery.
        object positionValue = safeDepth is double sd
            ? new { latitude, longitude, altitude = -Math.Abs(sd) }
            : new { latitude, longitude };
        return ResourceHttp.PutAsync(_http,
            _baseUrl.Combine(SignalKUrls.AnchorPositionPath),
            new { value = positionValue }, ct);
    }

    public Task<ApiResult> SetMaxRadiusAsync(int radiusMeters, CancellationToken ct = default) =>
        // Plugin expects an integer metre value; the SK-spec wrapper is
        // {value: N} (same shape as CourseApi.SetPointIndexAsync).
        ResourceHttp.PutAsync(_http,
            _baseUrl.Combine(SignalKUrls.AnchorMaxRadiusPath),
            new { value = radiusMeters }, ct);

    public Task<ApiResult> AutoSetRadiusAsync(CancellationToken ct = default) =>
        // Empty body per the v2.0.0 README. PostAsync sends
        // Content-Type: application/json which the plugin requires --
        // an empty string body would 415.
        ResourceHttp.PostAsync(_http,
            _baseUrl.Combine(SignalKUrls.AnchorAlarmAutoSetRadiusPath),
            new { }, ct);

    public Task<ApiResult> RaiseAsync(CancellationToken ct = default) =>
        // PUT {value: null} on anchor.position is the SK-spec way to
        // clear an anchor; the plugin handler treats null as raise.
        // Cast to (object?) is COMPILE-TIME -- the anonymous-type
        // member can't be inferred from a bare `null` (CS0815). The
        // resulting JSON shape is `{"value": null}` because the
        // default JsonSerializerOptions emit nulls. Tests pin the
        // JSON-null wire form; if a future PutAsync flips to
        // DefaultIgnoreCondition.WhenWritingNull we'd silently drop
        // the property and the plugin would 400.
        ResourceHttp.PutAsync(_http,
            _baseUrl.Combine(SignalKUrls.AnchorPositionPath),
            new { value = (object?)null }, ct);
}
