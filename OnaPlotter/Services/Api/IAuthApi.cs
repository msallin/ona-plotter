using OnaPlotter.Models;

namespace OnaPlotter.Services.Api;

/// <summary>SignalK auth-related calls.
///
/// <para><see cref="GetLoginStatusAsync"/> is the read-only probe
/// against <c>/skServer/loginStatus</c> (signalk-server-specific,
/// drives the Not-logged-in banner).</para>
///
/// <para><see cref="LoginAsync"/> + <see cref="LogoutAsync"/> wrap
/// the SK 1.7 spec endpoints under <c>/signalk/v1/auth/*</c>. Used
/// by the standalone-mode helm-supplied credentials flow so a tab
/// running on a different origin than the SK server can obtain a
/// session JWT without going through the SK admin UI.</para>
/// </summary>
public interface IAuthApi
{
    /// <summary>Probe the login-status endpoint. Returns null on
    /// 404 (endpoint absent), 5xx, or transport failure - caller
    /// treats null as "auth state unknown" and hides the banner.</summary>
    Task<LoginStatus?> GetLoginStatusAsync(CancellationToken ct = default);

    /// <summary>POST to <c>/signalk/v1/auth/login</c> with the
    /// supplied credentials. Returns the server's response on 200
    /// (carries the JWT + timeToLive). Any non-success
    /// (401 / 403 / 5xx / transport) returns null - the caller
    /// surfaces a helm-readable error.</summary>
    Task<LoginResult?> LoginAsync(string username, string password,
        CancellationToken ct = default);

    /// <summary>POST to <c>/signalk/v1/auth/logout</c>. Server
    /// invalidates the session token. Best-effort: failures
    /// (network, 401-when-already-out) don't bubble - the caller
    /// has already cleared the local token store on the way in.</summary>
    Task LogoutAsync(CancellationToken ct = default);
}
