using OnaPlotter.Models;
using OnaPlotter.Services.Api;
using OnaPlotter.Services.Settings;

namespace OnaPlotter.Services.Auth;

/// <summary>
/// Orchestrates the standalone-mode login flow on top of
/// <see cref="IAuthApi"/> + <see cref="ITokenStore"/> + the credential
/// settings on <see cref="IServerSettings"/>. Single entry point so
/// the Settings login dialog, the Program.cs startup hook, and the
/// (future) 401 retry path all funnel through the same persistence
/// rules.
///
/// <para>What lives here vs. what lives in <see cref="ITokenStore"/>:
/// the token store knows about JWT lifetime + storage; this service
/// knows about <i>credentials</i> (username + password) and the
/// "Remember session" / "Remember password" flags that gate
/// persistence. Splitting the two keeps each concern testable on its
/// own and means a future change to "what counts as a credential"
/// (PIN, certificate, etc.) lands here without touching the JWT
/// store.</para>
/// </summary>
public sealed class AuthSession
{
    private readonly IAuthApi _api;
    private readonly ITokenStore _tokens;
    private readonly IServerSettings _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<AuthSession> _logger;

    public AuthSession(IAuthApi api, ITokenStore tokens, IServerSettings settings,
        TimeProvider time, ILogger<AuthSession> logger)
    {
        _api = api;
        _tokens = tokens;
        _settings = settings;
        _time = time;
        _logger = logger;
    }

    /// <summary>Prompted by Program.cs after the settings + token
    /// store have been bootstrapped. Three branches:</summary>
    /// <list type="bullet">
    ///   <item>Token already valid: keep it, return true.</item>
    ///   <item>No valid token but we have stored creds AND
    ///   "Remember password" is on: silently re-login.</item>
    ///   <item>No valid token and no creds (or password persistence
    ///   off): return false. The Settings login dialog or the helm's
    ///   manual sign-in flow takes over.</item>
    /// </list>
    /// <returns>True when a usable token is in <see cref="ITokenStore"/>
    /// after this call.</returns>
    public async Task<bool> TryAutoLoginAsync(CancellationToken ct = default)
    {
        await _tokens.LoadAsync(ct);
        if (_tokens.IsValid) return true;

        if (!_settings.RememberPassword) return false;
        if (string.IsNullOrEmpty(_settings.StoredUsername)
            || string.IsNullOrEmpty(_settings.StoredPassword))
        {
            return false;
        }

        try
        {
            var ok = await LoginInternalAsync(
                _settings.StoredUsername,
                _settings.StoredPassword,
                rememberPassword: true,
                rememberSession: _settings.RememberSession,
                ct);
            return ok;
        }
        catch (Exception ex)
        {
            // Auto-login is best-effort; don't propagate. The helm
            // sees the Not-logged-in chip and can resign in via the
            // Settings dialog.
            _logger.LogWarning("[auth] auto-login failed: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>Helm-driven login from the Settings dialog. Always
    /// updates <see cref="IServerSettings.StoredUsername"/> so the
    /// field round-trips on the next visit. Password persistence
    /// honours <paramref name="rememberPassword"/> (matches the
    /// "Remember password" checkbox in the dialog). Returns true on
    /// 200 from the SK login endpoint, false otherwise.</summary>
    public Task<bool> LoginAsync(string username, string password,
        bool rememberPassword, CancellationToken ct = default) =>
        LoginInternalAsync(username, password, rememberPassword,
            rememberSession: _settings.RememberSession, ct);

    private async Task<bool> LoginInternalAsync(string username, string password,
        bool rememberPassword, bool rememberSession,
        CancellationToken ct)
    {
        var result = await _api.LoginAsync(username, password, ct);
        if (result is null || string.IsNullOrEmpty(result.Token))
        {
            return false;
        }

        var ttlSeconds = result.TimeToLive ?? DefaultJwtTtlSeconds;
        var expiresAt = _time.GetUtcNow().UtcDateTime
            + TimeSpan.FromSeconds(Math.Max(MinJwtTtlSeconds, ttlSeconds));

        await _tokens.SetAsync(result.Token, expiresAt, persist: rememberSession, ct);
        await _settings.SetStoredUsernameAsync(username);
        if (rememberPassword)
        {
            await _settings.SetRememberPasswordAsync(true);
            await _settings.SetStoredPasswordAsync(password);
        }
        else
        {
            // Make sure a previous "Remember password ON" doesn't
            // leave a stale value behind when the helm signs in
            // again with the checkbox unchecked.
            await _settings.SetRememberPasswordAsync(false);
            await _settings.SetStoredPasswordAsync("");
        }
        return true;
    }

    /// <summary>Explicit helm-driven logout. Order matters:
    ///   <list type="number">
    ///     <item>Clear the token store first (so an error mid-flow
    ///     can't leave us with a token + no credentials).</item>
    ///     <item>Wipe the persisted password if any (the username
    ///     stays - it's not a credential).</item>
    ///     <item>Best-effort POST to <c>/signalk/v1/auth/logout</c>
    ///     so the server invalidates its session record. Failure
    ///     here is silent: the JWT will time out within timeToLive
    ///     anyway.</item>
    ///   </list>
    /// </summary>
    public async Task LogoutAsync(CancellationToken ct = default)
    {
        await _tokens.ClearAsync(ct);
        if (!string.IsNullOrEmpty(_settings.StoredPassword))
        {
            await _settings.SetStoredPasswordAsync("");
        }
        await _api.LogoutAsync(ct);
    }

    /// <summary>Default JWT lifetime when the SK server omits
    /// <see cref="LoginResult.TimeToLive"/>. 24 h matches the
    /// signalk-server default; if the server actually issued a
    /// shorter-lived token the JWT's own <c>exp</c> claim will 401
    /// us first and the AuthHandler clears the token.</summary>
    public const int DefaultJwtTtlSeconds = 24 * 60 * 60;

    /// <summary>Floor on the persisted expiry. Guards against a
    /// server that returns 0 / negative timeToLive (one such
    /// signalk-server variant existed). 60 s minimum so a single
    /// in-flight request still uses the fresh token.</summary>
    public const int MinJwtTtlSeconds = 60;
}
