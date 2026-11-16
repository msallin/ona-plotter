using OnaPlotter.Utilities;

namespace OnaPlotter.Services.Api;

/// <summary>Thin HTTP client for arbitrary SignalK v1
/// <c>/signalk/v1/api/...</c> path reads. Used by one-off seed +
/// recovery flows (e.g. Map.razor's anchor-tide bootstrap, the WS
/// reconnect re-sync) where the live delta stream hasn't yet
/// delivered a value the page needs to render. Returns the raw value
/// JSON node so callers parse the path-specific shape themselves.</summary>
public sealed class PathApi : IPathApi
{
    private readonly HttpClient _http;
    private readonly ISignalKBaseUrl _baseUrl;
    private readonly ILogger<PathApi> _logger;

    public PathApi(HttpClient http, ISignalKBaseUrl baseUrl, ILogger<PathApi> logger)
    {
        _http = http;
        _baseUrl = baseUrl;
        _logger = logger;
    }

    public async Task<List<string>> GetAvailablePathsAsync(CancellationToken ct = default)
    {
        var url = _baseUrl.Combine(SignalKUrls.SelfVesselPath);
        var result = await ResourceHttp.GetJsonAsync(
            _http, url, _logger, static root => root.FlattenSignalKPaths(), ct);
        return result.Success && result.Value is not null ? result.Value : [];
    }
}
