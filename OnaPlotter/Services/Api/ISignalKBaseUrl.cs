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

    /// <summary>Scheme+host+port of the radar API endpoint. Defaults
    /// to <see cref="BaseUrl"/> when the radar provider runs behind
    /// the same reverse proxy as the main SK server, which is the
    /// common case. Overridable via <c>SignalK:RadarServerUrl</c>
    /// because mayara-server on OpenPlotter listens on its own port
    /// (6502 by default) and isn't always WS-proxied through 443.</summary>
    string RadarBaseUrl { get; }

    /// <summary>Combines <see cref="RadarBaseUrl"/> with a relative
    /// path. Use for every radar REST call; the main SK Combine()
    /// points at the wrong server when radar lives elsewhere.</summary>
    string CombineRadar(string path);
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

        // Radar endpoint. Defaults to the main SK BaseUrl when not
        // configured; overridable via SignalK:RadarServerUrl for
        // deployments where mayara-server listens on its own port
        // (OpenPlotter default 6502) rather than being fully proxied
        // behind the same TLS endpoint as the rest of SK.
        string? configuredRadar = configuration["SignalK:RadarServerUrl"];
        if (!string.IsNullOrWhiteSpace(configuredRadar))
        {
            var radarUri = new Uri(configuredRadar);
            RadarBaseUrl = $"{radarUri.Scheme}://{radarUri.Host}:{radarUri.Port}";
        }
        else
        {
            RadarBaseUrl = BaseUrl;
        }
    }

    public string BaseUrl { get; }
    public string RadarBaseUrl { get; }

    public Uri StreamUri(string subscribe = "none") => SignalKUrls.StreamWs(BaseUrl, subscribe);

    public string Combine(string path) => BaseUrl + path;
    public string CombineRadar(string path) => RadarBaseUrl + path;
}
