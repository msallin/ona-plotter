using Microsoft.AspNetCore.Components;

namespace OnaPlotter.Services.Api;

/// <summary>
/// Resolves the SignalK server base URL. Used by all API clients so the
/// resolution logic (config override vs. page origin vs. helm-supplied
/// standalone URL) lives in one place.
///
/// <para>The base URL can change at runtime when the helm flips
/// "Standalone mode" or edits the server URL on the Settings page.
/// Subscribers listen to <see cref="OnBaseUrlChanged"/> rather than
/// snapshotting the URL at construction. Most callers don't care -
/// they call <see cref="Combine"/> per request and pick up the new
/// URL automatically. <c>SignalkClient</c> is the exception: its
/// WebSocket is opened with a captured Uri at connect-time, so it
/// listens to the event and forces a reconnect.</para>
/// </summary>
public interface ISignalKBaseUrl
{
    /// <summary>Scheme+host+port of the SignalK server (no trailing slash, no path).</summary>
    string BaseUrl { get; }

    /// <summary>Fires when <see cref="BaseUrl"/> resolves to a different
    /// origin than the previous resolution. Filtered: settings-changed
    /// fan-outs that don't touch the URL (theme, alarms, ...) do NOT
    /// fire this event. SignalkClient subscribes to drive reconnect.</summary>
    event Action? OnBaseUrlChanged;

    /// <summary>WebSocket stream URL derived from <see cref="BaseUrl"/>.</summary>
    Uri StreamUri(string subscribe = "none");

    /// <summary>Combines the base URL with a relative SignalK API path.</summary>
    string Combine(string path);
}

public sealed class SignalKBaseUrl : ISignalKBaseUrl
{
    private readonly IAppSettings? _settings;
    private readonly string _fallbackUrl;
    private string _currentUrl;

    public event Action? OnBaseUrlChanged;

    /// <summary>DI ctor. <paramref name="settings"/> is optional so the
    /// existing test fixtures (which construct SignalKBaseUrl directly
    /// with just config + nav) keep working without a fake settings.
    /// In production DI wires the 3-arg form; tests can opt in by
    /// passing a settings stub when they want to exercise the
    /// standalone-mode path.</summary>
    public SignalKBaseUrl(IConfiguration configuration, NavigationManager nav,
        IAppSettings? settings = null)
    {
        // Fallback URL: the original resolution (config override OR
        // page origin via NavigationManager). Cached at construction
        // time because neither input changes after boot.
        string? configured = configuration["SignalK:ServerUrl"];
        string url = string.IsNullOrWhiteSpace(configured) || configured == "auto"
            ? nav.BaseUri
            : configured;
        var uri = new Uri(url);
        _fallbackUrl = $"{uri.Scheme}://{uri.Host}:{uri.Port}";

        _settings = settings;
        _currentUrl = ResolveBaseUrl();

        if (_settings is not null)
        {
            _settings.OnSettingsChanged += HandleSettingsChanged;
        }
    }

    public string BaseUrl => _currentUrl;

    public Uri StreamUri(string subscribe = "none") => SignalKUrls.StreamWs(BaseUrl, subscribe);

    public string Combine(string path) => BaseUrl + path;

    /// <summary>Pick the active origin. Standalone mode wins when both
    /// the flag is on AND the URL field is non-empty; otherwise we
    /// fall through to the auto-detected origin so an empty field
    /// can't break the app.</summary>
    private string ResolveBaseUrl()
    {
        if (_settings is { StandaloneMode: true } s
            && !string.IsNullOrWhiteSpace(s.StandaloneServerUrl))
        {
            return s.StandaloneServerUrl;
        }
        return _fallbackUrl;
    }

    /// <summary>Settings-changed fan-out subscriber. Filters to URL-
    /// only changes so a theme flip doesn't churn every API client +
    /// the WebSocket. Race-safe: the diff is single-threaded by the
    /// settings event's invocation list.</summary>
    private void HandleSettingsChanged()
    {
        var resolved = ResolveBaseUrl();
        if (string.Equals(resolved, _currentUrl, StringComparison.Ordinal)) return;
        _currentUrl = resolved;
        OnBaseUrlChanged?.Invoke();
    }
}
