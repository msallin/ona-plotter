namespace OnaPlotter.Services.Api;

public sealed class AutopilotApi : IAutopilotApi
{
    private readonly HttpClient _http;
    private readonly ISignalKBaseUrl _baseUrl;

    public AutopilotApi(HttpClient http, ISignalKBaseUrl baseUrl)
    {
        _http = http;
        _baseUrl = baseUrl;
    }

    public Task<ApiResult> SetStateAsync(string state, CancellationToken ct = default) =>
        ResourceHttp.PutAsync(_http, _baseUrl.Combine(SignalKUrls.AutopilotStatePath),
            new { value = state }, ct);

    public Task<ApiResult> AdjustHeadingAsync(double deltaDeg, CancellationToken ct = default)
    {
        var body = new { value = deltaDeg * Math.PI / 180.0 };
        return ResourceHttp.PutAsync(_http, _baseUrl.Combine(SignalKUrls.AutopilotAdjustHeadingPath), body, ct);
    }
}
