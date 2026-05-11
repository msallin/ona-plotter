using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the chip-href contract for the not-logged-in chip. The SK
/// admin Login component (signalk/signalk-server PR #2647) only
/// honours <c>redirect</c> when it passes the same safety rules as
/// the server's <c>isSafeRelativeUrl</c>; rendering a URL the SK
/// admin would silently refuse would be invisible at the helm and
/// surface as "I logged in but didn't land back on OnaPlotter".
/// Every safety branch is pinned so a future refactor of
/// <see cref="SkAdminLoginUrl"/> can't drop one quietly.
/// </summary>
public class SkAdminLoginUrlTests
{
    [Test]
    public async Task Build_SameOrigin_Plugin_Mount_Appends_Encoded_Redirect()
    {
        // Typical install: OnaPlotter mounted at /signalk-onaplotter/
        // on the SK server origin. Helm taps the chip while on the
        // History sub-route; redirect must round-trip that sub-route
        // so they land on History after sign-in, not the map default.
        var url = SkAdminLoginUrl.Build(
            skBaseUrl: "https://openplotter.local",
            pageUri: "https://openplotter.local/signalk-onaplotter/history");

        await Assert.That(url).IsEqualTo(
            "https://openplotter.local/admin/#/login?redirect="
            + Uri.EscapeDataString("/signalk-onaplotter/history"));
    }

    [Test]
    public async Task Build_SameOrigin_Root_Mount_Appends_Slash_Redirect()
    {
        // Less common but valid: OnaPlotter served from /. Redirect
        // value is just "/", which still passes the SK validation
        // (single leading slash, no //, no backslash, no control).
        var url = SkAdminLoginUrl.Build(
            skBaseUrl: "https://openplotter.local",
            pageUri: "https://openplotter.local/");

        await Assert.That(url).IsEqualTo(
            "https://openplotter.local/admin/#/login?redirect="
            + Uri.EscapeDataString("/"));
    }

    [Test]
    public async Task Build_PreservesQueryString_In_Redirect()
    {
        // Pages that carry query string state (filters, scrubber
        // position) must round-trip - else the helm lands on the
        // page with state reset. PathAndQuery captures the query;
        // EscapeDataString turns '?' into %3F so the SK admin's
        // hash-query parser doesn't mistake it for the start of a
        // NEW query.
        var url = SkAdminLoginUrl.Build(
            skBaseUrl: "https://openplotter.local",
            pageUri: "https://openplotter.local/signalk-onaplotter/history?from=2026-04-01");

        await Assert.That(url).Contains("?redirect=");
        await Assert.That(url).Contains(Uri.EscapeDataString("/signalk-onaplotter/history?from=2026-04-01"));
        // Ensure the question mark inside the path got encoded - a
        // raw '?' would split the URL and the SK admin would parse
        // 'from=...' as a sibling param of redirect.
        await Assert.That(url).DoesNotContain("history?from");
    }

    [Test]
    public async Task Build_TrailingSlash_On_SkBaseUrl_Is_Tolerated()
    {
        // The DI fallback URL is already TrimEnd('/')'d, but a helm-
        // typed Standalone URL might come in with a trailing slash.
        // The chip mustn't render //admin/.
        var url = SkAdminLoginUrl.Build(
            skBaseUrl: "https://openplotter.local/",
            pageUri: "https://openplotter.local/signalk-onaplotter/");

        await Assert.That(url).StartsWith("https://openplotter.local/admin/#/login?redirect=");
        await Assert.That(url).DoesNotContain("//admin/");
    }

    [Test]
    public async Task Build_DifferentHost_Drops_Redirect()
    {
        // Standalone mode pointing at a remote SK: page origin is
        // (say) localhost:5000 dev host, SK is openplotter.local.
        // A redirect targeting /signalk-onaplotter/ would not exist
        // on the SK origin, so SK would refuse it; render the bare
        // login URL instead (the existing visibilitychange handler
        // covers the chip-clear-on-return case).
        var url = SkAdminLoginUrl.Build(
            skBaseUrl: "https://openplotter.local",
            pageUri: "http://localhost:5000/");

        await Assert.That(url).IsEqualTo("https://openplotter.local/admin/#/login");
        await Assert.That(url).DoesNotContain("redirect=");
    }

    [Test]
    public async Task Build_DifferentPort_Drops_Redirect()
    {
        // Same host but different port also counts as cross-origin
        // for browsers' same-origin policy and the SK validation.
        var url = SkAdminLoginUrl.Build(
            skBaseUrl: "https://openplotter.local:3000",
            pageUri: "https://openplotter.local:8080/");

        await Assert.That(url).DoesNotContain("redirect=");
    }

    [Test]
    public async Task Build_DifferentScheme_Drops_Redirect()
    {
        // http vs https is also cross-origin; redirect would be
        // refused. Defensive: a mis-configured Standalone URL that
        // uses http for a https-only SK shouldn't break the chip.
        var url = SkAdminLoginUrl.Build(
            skBaseUrl: "https://openplotter.local",
            pageUri: "http://openplotter.local/");

        await Assert.That(url).DoesNotContain("redirect=");
    }

    [Test]
    public async Task Build_Empty_PageUri_Returns_Bare_Login_Url()
    {
        // Defensive: Nav.Uri is normally non-empty, but a calling
        // site that hasn't initialised yet shouldn't crash the chip.
        var url = SkAdminLoginUrl.Build(
            skBaseUrl: "https://openplotter.local",
            pageUri: "");

        await Assert.That(url).IsEqualTo("https://openplotter.local/admin/#/login");
    }

    [Test]
    public async Task Build_Malformed_PageUri_Returns_Bare_Login_Url()
    {
        // Defensive: a URI string that fails to parse must not throw
        // out of the chip render path. Same fallback as the empty
        // case.
        var url = SkAdminLoginUrl.Build(
            skBaseUrl: "https://openplotter.local",
            pageUri: "not a url");

        await Assert.That(url).IsEqualTo("https://openplotter.local/admin/#/login");
    }

    // --- IsSafeRelativeUrl: mirrors signalk-server's validation ---
    // The SK admin client refuses to honour `redirect` when these
    // rules fail. Pin every branch so the OnaPlotter chip never
    // sends a value the SK admin would silently drop.

    [Test]
    public async Task IsSafeRelativeUrl_SinglePath_IsAccepted()
    {
        await Assert.That(SkAdminLoginUrl.IsSafeRelativeUrl("/signalk-onaplotter/")).IsTrue();
        await Assert.That(SkAdminLoginUrl.IsSafeRelativeUrl("/")).IsTrue();
        await Assert.That(SkAdminLoginUrl.IsSafeRelativeUrl("/a/b/c?q=1")).IsTrue();
    }

    [Test]
    public async Task IsSafeRelativeUrl_Empty_OrNoLeadingSlash_Rejected()
    {
        // No leading slash means an absolute URL or a relative path
        // a browser would resolve against the current document URL -
        // could end up off-origin.
        await Assert.That(SkAdminLoginUrl.IsSafeRelativeUrl("")).IsFalse();
        await Assert.That(SkAdminLoginUrl.IsSafeRelativeUrl("signalk-onaplotter/")).IsFalse();
        await Assert.That(SkAdminLoginUrl.IsSafeRelativeUrl("https://evil.example.com/")).IsFalse();
    }

    [Test]
    public async Task IsSafeRelativeUrl_DoubleSlash_Rejected()
    {
        // //evil.example.com/ is a protocol-relative URL the browser
        // resolves against current scheme; bypasses the relative-path
        // check otherwise.
        await Assert.That(SkAdminLoginUrl.IsSafeRelativeUrl("//evil.example.com/")).IsFalse();
        await Assert.That(SkAdminLoginUrl.IsSafeRelativeUrl("//")).IsFalse();
    }

    [Test]
    public async Task IsSafeRelativeUrl_Backslash_Rejected()
    {
        // Some browsers normalise backslash to slash, opening another
        // bypass vector. SK server's regex blocks them outright.
        await Assert.That(SkAdminLoginUrl.IsSafeRelativeUrl(@"/a\b")).IsFalse();
        await Assert.That(SkAdminLoginUrl.IsSafeRelativeUrl(@"\evil")).IsFalse();
    }

    [Test]
    public async Task IsSafeRelativeUrl_ControlChars_Rejected()
    {
        // \r\n could split-inject headers in a downstream redirect;
        // \0 / \t / DEL all rejected to match the SK server's pattern.
        await Assert.That(SkAdminLoginUrl.IsSafeRelativeUrl("/a\r\nb")).IsFalse();
        await Assert.That(SkAdminLoginUrl.IsSafeRelativeUrl("/a\tb")).IsFalse();
        await Assert.That(SkAdminLoginUrl.IsSafeRelativeUrl("/a b")).IsFalse();
        await Assert.That(SkAdminLoginUrl.IsSafeRelativeUrl("/ab")).IsFalse();
    }

    [Test]
    public async Task IsSafeRelativeUrl_AdminPath_Rejected_LoopGuard()
    {
        // Redirecting back into /admin/ after a successful login is
        // pointless at best (helm lands on the admin home, not
        // OnaPlotter) and a loop at worst (lands on /admin/#/login).
        // The SK admin's own client check rejects the login route
        // explicitly; we widen it to all /admin/ paths because
        // OnaPlotter has no business sending the helm there.
        await Assert.That(SkAdminLoginUrl.IsSafeRelativeUrl("/admin/")).IsFalse();
        await Assert.That(SkAdminLoginUrl.IsSafeRelativeUrl("/admin/#/login")).IsFalse();
        await Assert.That(SkAdminLoginUrl.IsSafeRelativeUrl("/admin")).IsFalse();
        // Case-insensitive: a request for /ADMIN/ would still hit
        // the admin route on case-insensitive filesystems / proxies.
        await Assert.That(SkAdminLoginUrl.IsSafeRelativeUrl("/Admin/")).IsFalse();
    }
}
