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
        // SK convention for the position object: { latitude, longitude,
        // altitude } where altitude is metres ABOVE the SK datum.
        // Anchors sit on the seabed -- altitude = -|depth|. When the
        // helm has no depth source we omit altitude entirely; the v2
        // plugin tolerates the missing field and derives one from the
        // depth path itself when one is published. Mixed anonymous
        // types are stored as `object` so System.Text.Json picks them
        // up via runtime type discovery.
        object positionValue = depthMeters is double d
            ? new { latitude, longitude, altitude = -Math.Abs(d) }
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
        // Cast to (object?) so System.Text.Json emits JSON null rather
        // than dropping the property (which would 400 on the plugin
        // side: "missing value field").
        ResourceHttp.PutAsync(_http,
            _baseUrl.Combine(SignalKUrls.AnchorPositionPath),
            new { value = (object?)null }, ct);
}
