namespace OnaPlotter.Services.Settings;

/// <summary>
/// Narrow surface for server-connection consumers. Drives the
/// "Standalone mode" feature: when on, OnaPlotter speaks to the
/// SignalK server via the helm-supplied <see cref="StandaloneServerUrl"/>
/// instead of the same-origin assumption baked into the bundled
/// SignalK webapp deploy. Lets the app run from any browser tab
/// (GitHub Pages, file://, a separate static-host) against a remote
/// SK server when CORS is enabled on that server.
///
/// <para>Carved from <see cref="IAppSettings"/> as part of ARCH-003;
/// <see cref="ISignalKBaseUrl"/> binds against this narrow surface so
/// the URL-resolver doesn't have to know about night-mode or chart
/// filters.</para>
///
/// <para>WS auth-token + login state intentionally do NOT live here -
/// the next PR will add a separate <c>ITokenStore</c> for those so
/// "where do I point at?" stays distinct from "how do I authenticate?".
/// </para>
/// </summary>
public interface IServerSettings
{
    /// <summary>When true, OnaPlotter ignores its compile-time +
    /// page-origin SignalK URL and uses <see cref="StandaloneServerUrl"/>
    /// instead. Default false: an embedded webapp install (the helm-
    /// at-the-helm common case) keeps its auto-detected origin
    /// behaviour. Switching this on without a valid URL falls back to
    /// the auto-detected origin (we don't break the app over an empty
    /// field), but the Settings page surfaces the gap with a hint.</summary>
    bool StandaloneMode { get; }

    /// <summary>Origin (scheme + host + port, no path) of the SignalK
    /// server when <see cref="StandaloneMode"/> is on. Empty string
    /// when unset. Set via the Advanced > Standalone mode card on
    /// the Settings page; persisted in localStorage so a reload
    /// re-uses it. Validated at write-time: parses as an absolute Uri,
    /// http(s) scheme only, path stripped to origin. A bad value is
    /// rejected (the setter throws); the UI guards before calling.</summary>
    string StandaloneServerUrl { get; }

    /// <summary>Toggle the standalone-mode flag. Persists + fires
    /// <see cref="IAppSettings.OnSettingsChanged"/> so
    /// <c>SignalKBaseUrl</c> can re-resolve the active origin and
    /// <c>SignalkClient</c> can drop + reopen its WebSocket.</summary>
    Task SetStandaloneModeAsync(bool value);

    /// <summary>Set the standalone server URL. Caller is responsible
    /// for validating the input; this setter normalises (strips path /
    /// query / fragment, lower-cases the host) and persists. Empty
    /// string is allowed (clears the field).</summary>
    Task SetStandaloneServerUrlAsync(string value);

    /// <summary>"Remember me" preference: persist the JWT returned by
    /// <c>/signalk/v1/auth/login</c> so a tab reload reuses it until
    /// expiry. Default true (so the helm doesn't have to re-type their
    /// password every time the tab refreshes). Independent of
    /// <see cref="RememberPassword"/>: a session-only persistence is
    /// already a meaningful "remember me" - the helm only re-types
    /// when the JWT expires (typically days, server-configurable).</summary>
    bool RememberSession { get; }

    /// <summary>OPT-IN: also persist the username + password in
    /// localStorage so the auto-login flow can re-issue a fresh JWT
    /// after one expires WITHOUT prompting the helm. Default false;
    /// the Settings UI surfaces a clear "stored in plain text" warning
    /// before turning this on. Storing the password is a real risk -
    /// any script running on this origin (an XSS, a third-party
    /// dependency) can read the value - so the flag stays off-by-
    /// default and the session-only path is the recommended posture.</summary>
    bool RememberPassword { get; }

    /// <summary>Last-used username from the standalone-mode login
    /// dialog. Persisted unconditionally (it's not a credential on its
    /// own) so the helm doesn't re-type it on every visit. Cleared on
    /// explicit logout.</summary>
    string StoredUsername { get; }

    /// <summary>Helm-supplied password used for the auto-login flow.
    /// Persists IF AND ONLY IF <see cref="RememberPassword"/> is true.
    /// Empty string when not stored. Caller (the login dialog) is
    /// responsible for matching the persist-or-not flag to the
    /// "Remember password" checkbox state.</summary>
    string StoredPassword { get; }

    Task SetRememberSessionAsync(bool value);
    Task SetRememberPasswordAsync(bool value);
    Task SetStoredUsernameAsync(string value);
    /// <summary>Persist the helm's password. Caller MUST gate on
    /// <see cref="RememberPassword"/>; passing a non-empty password
    /// while the flag is off writes the value to storage. The setter
    /// itself doesn't check the flag - it's the helm's choice via the
    /// UI's "Remember password" checkbox. Pass an empty string to
    /// clear (used on explicit logout / flag flipped off).</summary>
    Task SetStoredPasswordAsync(string value);
}
