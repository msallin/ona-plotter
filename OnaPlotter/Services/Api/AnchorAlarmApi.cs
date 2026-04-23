namespace OnaPlotter.Services.Api;

/// <summary>Default <see cref="IAnchorAlarmApi"/> hitting the
/// plugin's POST endpoints on the same host as the main SK server.
/// </summary>
public sealed class AnchorAlarmApi : IAnchorAlarmApi
{
    private readonly HttpClient _http;
    private readonly ISignalKBaseUrl _baseUrl;

    // The plugin registers its routes under /plugins/anchoralarm/*;
    // this matches the name used in the README's curl examples and
    // the plugin's package.json. If a future release renames the
    // prefix we'd need to make this configurable, but no other SK
    // plugin the plotter talks to has done that in practice.
    private const string RaiseAnchorPath = "/plugins/anchoralarm/raiseAnchor";

    public AnchorAlarmApi(HttpClient http, ISignalKBaseUrl baseUrl)
    {
        _http = http;
        _baseUrl = baseUrl;
    }

    public Task<ApiResult> RaiseAsync(CancellationToken ct = default) =>
        // Body is empty per the plugin docs; we still send {} because
        // PostAsync sets Content-Type: application/json and the plugin
        // expects that header.
        ResourceHttp.PostAsync(_http, _baseUrl.Combine(RaiseAnchorPath),
            new { }, ct);
}
