namespace OnaPlotter.Utilities;

/// <summary>
/// Builds the SignalK admin "login" link with the optional
/// <c>redirect</c> hash-query parameter that signalk-server v2.x
/// honours via signalk/signalk-server PR #2647. The admin Login
/// component reads <c>redirect</c> from the hash query
/// (<c>/admin/#/login?redirect=/signalk-onaplotter/</c>) and, after
/// a successful form login or OIDC round-trip, navigates the
/// browser to the target path. This lets a webapp (OnaPlotter)
/// delegate authentication to the SK admin and land the helm back
/// on the same OnaPlotter page they came from, with the session
/// cookie in place.
///
/// <para>Lives separately from <c>MainLayout.razor</c> so the
/// branching + validation can be unit-tested without standing up
/// a Blazor host. The SK admin client validates the redirect
/// against the same rules as the server's <c>isSafeRelativeUrl</c>;
/// we mirror them here so the chip never renders a URL the SK
/// admin would later refuse (would silently no-op the redirect).</para>
/// </summary>
public static class SkAdminLoginUrl
{
    /// <summary>Hash-routed SK admin login path appended to the
    /// SK base origin. Lives here (rather than as a magic string)
    /// so a future move (e.g. <c>/admin2/#/login</c>) is one edit.</summary>
    public const string LoginPath = "/admin/#/login";

    /// <summary>
    /// Build the chip href. Returns the bare login URL when the
    /// SK origin differs from the page origin (the SK admin would
    /// refuse the cross-origin redirect anyway) or when the
    /// page's path fails the relative-URL safety rules.
    /// </summary>
    /// <param name="skBaseUrl">SK server origin, e.g.
    /// <c>https://openplotter.local</c>. Must include scheme + host;
    /// trailing slash is tolerated.</param>
    /// <param name="pageUri">Full current page URL, typically
    /// <c>NavigationManager.Uri</c> from the calling component.</param>
    /// <returns>
    /// Same-origin: <c>{skBaseUrl}/admin/#/login?redirect={encoded path}</c>.
    /// Cross-origin / malformed / unsafe: <c>{skBaseUrl}/admin/#/login</c>.
    /// </returns>
    public static string Build(string skBaseUrl, string pageUri)
    {
        string trimmed = (skBaseUrl ?? string.Empty).TrimEnd('/');
        string loginUrl = trimmed + LoginPath;

        if (string.IsNullOrEmpty(trimmed) || string.IsNullOrEmpty(pageUri))
            return loginUrl;

        Uri sk, page;
        try
        {
            sk = new Uri(trimmed);
            page = new Uri(pageUri);
        }
        catch (UriFormatException) { return loginUrl; }

        if (!IsSameOrigin(sk, page)) return loginUrl;

        // PathAndQuery excludes the fragment, which is exactly what
        // we want: a Blazor SPA route like "#/foo" wouldn't survive
        // the SK server's relative-URL rules anyway, and the OnaPlotter
        // routing is path-based (no hash). For a typical install at
        // /signalk-onaplotter/ this yields "/signalk-onaplotter/" or
        // "/signalk-onaplotter/history" etc.
        string path = page.PathAndQuery;
        if (!IsSafeRelativeUrl(path)) return loginUrl;

        return loginUrl + "?redirect=" + Uri.EscapeDataString(path);
    }

    /// <summary>Mirror of the SK admin client's safety check (in
    /// turn mirroring the server's <c>isSafeRelativeUrl</c>): must be
    /// a single-leading-slash relative path, no protocol-relative
    /// <c>//</c>, no backslashes, no control characters, and must not
    /// already be the login route (loop guard).</summary>
    internal static bool IsSafeRelativeUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        if (url[0] != '/') return false;
        if (url.Length >= 2 && url[1] == '/') return false;
        foreach (char c in url)
        {
            if (c == '\\') return false;
            if (c < 0x20 || c == 0x7F) return false;
        }
        // Loop guard: redirecting back to /admin/... after login on
        // /admin/#/login would either land on the admin home (harmless
        // but the redirect added no value) or, in the worst case, the
        // login page itself (loop). The SK server's client check
        // explicitly rejects the login route; we tighten it to all
        // admin paths because OnaPlotter has no reason to redirect
        // anywhere under /admin/.
        if (url.StartsWith("/admin/", StringComparison.OrdinalIgnoreCase)
            || url.Equals("/admin", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return true;
    }

    private static bool IsSameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;
}
