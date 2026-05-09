namespace OnaPlotter.Services.Auth;

/// <summary>
/// Holds the SignalK session token returned by
/// <c>/signalk/v1/auth/login</c>. Separate from <see cref="IAppSettings"/>
/// because token lifecycle ("we have a valid JWT until X UTC") is a
/// distinct concern from helm preferences ("show big type", "alarm at
/// 2 m"). Auth code subscribes to <see cref="OnTokenChanged"/>; settings
/// code never sees the token.
///
/// <para>Two storage strategies coexist:</para>
/// <list type="bullet">
///   <item><b>Remember session ON (default when standalone-auth is in
///   use):</b> persist token + expiry to <see cref="IKeyValueStore"/>.
///   A reload reuses it until expiry.</item>
///   <item><b>Remember session OFF:</b> token stays in memory only; a
///   reload prompts the helm to sign in again.</item>
/// </list>
///
/// <para>The token does NOT get stored when the helm hasn't opted in.
/// Storing the password is governed by separate settings flags
/// (<see cref="IServerSettings.RememberPassword"/>) and lives on the
/// settings store, not here - this service is JWT-only.</para>
/// </summary>
public interface ITokenStore
{
    /// <summary>The current JWT, or null when none / expired / cleared.
    /// <c>null</c> after <see cref="ClearAsync"/> until the next login.
    /// Reads through <see cref="IsValid"/> for "use this for an HTTP
    /// call" decisions; <see cref="Token"/> on its own can be expired.</summary>
    string? Token { get; }

    /// <summary>UTC timestamp when <see cref="Token"/> expires.
    /// Captured from the JWT's <c>exp</c> claim (or from the
    /// <c>timeToLive</c> response field as fallback). Null when no
    /// token is set.</summary>
    DateTime? ExpiresAtUtc { get; }

    /// <summary>"Token is set AND not expired" - use this in HTTP-call
    /// decision sites. Slightly conservative: counts a token expiring
    /// in the next 30 s as already expired so a request in flight
    /// doesn't 401 on the wire because of clock skew. The 30 s skew
    /// guard mirrors the standard JWT clock-skew default in
    /// .NET's TokenValidationParameters and absorbs typical helm-tablet
    /// vs. SK-server time drift.</summary>
    bool IsValid { get; }

    /// <summary>Fires after <see cref="Token"/> moves between {null,
    /// non-null} OR after a re-login replaces the value with a
    /// different JWT. The same JWT being re-set is a no-op.
    /// Subscribers: WS reconnect (token in URL changes), HTTP handler
    /// (no diff needed - reads on every request).</summary>
    event Action? OnTokenChanged;

    /// <summary>Replace the in-memory token + expiry. When
    /// <paramref name="persist"/> is true, also writes the pair to
    /// <see cref="IKeyValueStore"/> under the auth.* keys so a reload
    /// reuses the token. Caller is responsible for matching
    /// <paramref name="persist"/> to the helm's "Remember session"
    /// preference - the store doesn't know about that flag.</summary>
    Task SetAsync(string token, DateTime expiresAtUtc, bool persist,
        CancellationToken ct = default);

    /// <summary>Drop the in-memory token AND remove any persisted copy.
    /// Idempotent - clearing an already-empty store is a no-op (no
    /// event fired).</summary>
    Task ClearAsync(CancellationToken ct = default);

    /// <summary>One-shot bootstrap. Reads the persisted token if any
    /// from <see cref="IKeyValueStore"/>. If the persisted token is
    /// already expired, drops it (storage stays clean). Safe to call
    /// multiple times; second call is a no-op.</summary>
    Task LoadAsync(CancellationToken ct = default);
}
