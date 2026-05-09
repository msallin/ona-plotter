using System.Net;
using System.Net.Http.Headers;

namespace OnaPlotter.Services.Auth;

/// <summary>
/// HTTP message handler that attaches <c>Authorization: Bearer &lt;jwt&gt;</c>
/// to every outgoing request when <see cref="ITokenStore"/> has a
/// valid token. Inserted between the OnaPlotter HttpClient and the
/// platform handler so all *Api clients (RouteApi, NotificationsApi,
/// ChartApi, ...) inherit the auth attachment for free.
///
/// <para>Skips attachment in three cases:</para>
/// <list type="bullet">
///   <item>No token (same-origin install relies on the browser
///   session cookie that signalk-server sets at login).</item>
///   <item>Token expired (the cached value would just 401; let the
///   request go anonymous so the helm sees the right "not signed in"
///   response).</item>
///   <item>Caller already attached an Authorization header (the auth
///   login request itself, where the body carries the credentials).</item>
/// </list>
///
/// <para>On a 401 response, clears the token. The next render of the
/// not-signed-in chip + auto-login attempt picks up from the cleared
/// state. We deliberately do NOT silently re-login from inside the
/// handler: a 401 mid-action means the helm's saved session expired,
/// and replaying the original request behind their back risks a
/// duplicate write (e.g. the route POST going through twice). Surfacing
/// the 401 to the API client lets the caller decide whether to retry
/// after a re-auth.</para>
/// </summary>
public sealed class AuthHandler : DelegatingHandler
{
    private readonly ITokenStore _tokens;

    public AuthHandler(ITokenStore tokens)
    {
        _tokens = tokens;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.Authorization is null && _tokens.IsValid)
        {
            // _tokens.Token is non-null when IsValid is true (see
            // TokenStore.IsValid implementation), so the bang here is
            // safe; null-forgiving keeps the analyzer quiet.
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer", _tokens.Token!);
        }

        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized && _tokens.Token is not null)
        {
            // Saved session expired or was invalidated server-side.
            // ClearAsync's persistence wipe is best-effort; even if it
            // fails the in-memory copy is gone, so the next request
            // goes anonymous and the not-signed-in chip flips. The
            // OnTokenChanged event fans out to the WS reconnect loop
            // so the stream URL drops the stale ?token=...
            await _tokens.ClearAsync(cancellationToken);
        }

        return response;
    }
}
