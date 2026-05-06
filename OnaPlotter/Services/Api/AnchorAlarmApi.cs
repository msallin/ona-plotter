namespace OnaPlotter.Services.Api;

/// <summary>Default <see cref="IAnchorAlarmApi"/>. See the interface
/// docstring for the two-step flow rationale + endpoint choice.</summary>
public sealed class AnchorAlarmApi : IAnchorAlarmApi
{
    private readonly HttpClient _http;
    private readonly ISignalKBaseUrl _baseUrl;

    public AnchorAlarmApi(HttpClient http, ISignalKBaseUrl baseUrl)
    {
        _http = http;
        _baseUrl = baseUrl;
    }

    public Task<ApiResult> DropAsync(CancellationToken ct = default) =>
        // Empty JSON body per the plugin's REST docs. POST without
        // a Content-Type: application/json header would 415, hence
        // PostAsync (which ResourceHttp wraps with the right header).
        // Plugin reads current GPS internally; we don't ship lat/lon.
        ResourceHttp.PostAsync(_http,
            _baseUrl.Combine(SignalKUrls.AnchorAlarmDropAnchorPath),
            new { }, ct);

    public Task<ApiResult> SetMaxRadiusAsync(int radiusMeters, CancellationToken ct = default) =>
        // PUT body wrapper: {value: N} (same shape as
        // CourseApi.SetPointIndexAsync). Plugin treats this as
        // "set the alarm radius to N metres"; subsequent calls
        // adjust the value in place without raising.
        ResourceHttp.PutAsync(_http,
            _baseUrl.Combine(SignalKUrls.AnchorMaxRadiusPath),
            new { value = radiusMeters }, ct);

    public Task<ApiResult> RaiseAsync(CancellationToken ct = default) =>
        // PUT {value: null} on anchor.position is the SK-spec way
        // to clear an anchor. Cast to (object?) is COMPILE-TIME --
        // bare `null` has no inferable type for an anonymous-type
        // member (CS0815). Default JsonSerializerOptions emit JSON
        // null (tests pin the wire shape).
        ResourceHttp.PutAsync(_http,
            _baseUrl.Combine(SignalKUrls.AnchorPositionPath),
            new { value = (object?)null }, ct);
}
