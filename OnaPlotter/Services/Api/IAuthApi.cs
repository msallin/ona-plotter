using OnaPlotter.Models;

namespace OnaPlotter.Services.Api;

/// <summary>Read-only auth probe against
/// <c>/skServer/loginStatus</c>. Used by the not-logged-in banner
/// to warn the helm before they attempt a save that would 401.
///
/// <para>Endpoint is signalk-server-specific (not part of the SK
/// spec) so a third-party server may 404 - the implementation
/// returns null in that case and the banner stays hidden, which
/// is honest for "we can't tell".</para>
/// </summary>
public interface IAuthApi
{
    /// <summary>Probe the login-status endpoint. Returns null on
    /// 404 (endpoint absent), 5xx, or transport failure - caller
    /// treats null as "auth state unknown" and hides the banner.</summary>
    Task<LoginStatus?> GetLoginStatusAsync(CancellationToken ct = default);
}
