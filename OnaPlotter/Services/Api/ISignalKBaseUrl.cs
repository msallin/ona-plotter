using Microsoft.AspNetCore.Components;

namespace OnaPlotter.Services.Api;

/// <summary>
/// Resolves the SignalK server base URL. Used by all API clients so the
/// resolution logic (config override vs. page origin) lives in one place.
/// </summary>
public interface ISignalKBaseUrl
{
    /// <summary>Scheme+host+port of the SignalK server (no trailing slash, no path).</summary>
    string BaseUrl { get; }

    /// <summary>WebSocket stream URL derived from <see cref="BaseUrl"/>.</summary>
    Uri StreamUri(string subscribe = "none");

    /// <summary>Combines the base URL with a relative SignalK API path.</summary>
    string Combine(string path);
}

public sealed class SignalKBaseUrl : ISignalKBaseUrl
{
    public SignalKBaseUrl(IConfiguration configuration, NavigationManager nav)
    {
        string? configured = configuration["SignalK:ServerUrl"];
        string url = string.IsNullOrWhiteSpace(configured) || configured == "auto"
            ? nav.BaseUri
            : configured;

        var uri = new Uri(url);
        BaseUrl = $"{uri.Scheme}://{uri.Host}:{uri.Port}";
    }

    public string BaseUrl { get; }

    public Uri StreamUri(string subscribe = "none") => SignalKUrls.StreamWs(BaseUrl, subscribe);

    public string Combine(string path) => BaseUrl + path;
}
