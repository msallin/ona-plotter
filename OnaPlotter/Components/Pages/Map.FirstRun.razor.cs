namespace OnaPlotter.Components.Pages;

/// <summary>
/// Map page code-behind partial: first-visit onboarding overlays.
/// Covers two independent bits of UI:
///   1. Welcome card, shown once ever per device on first visit.
///      Explains the core workflow (enable charts, set depth alarm,
///      long-press for context menu). Dismissed via "Got it".
///   2. Touch coachmark, shown once per device to teach the long-
///      press context menu gesture. Auto-dismisses after 6 s so a
///      glancing helm still catches it.
///
/// State persisted behind two KV flags so a page reload or a device
/// sync doesn't re-show the cards. The welcome card suppresses the
/// coachmark on first visit (both would pile up), then chains the
/// coachmark after dismissal so first-run users still learn the
/// long-press gesture.
///
/// Razor source-generates the Map class from Map.razor; this partial
/// composes into the same class. Fields declared here are visible
/// from the markup and other partials.
/// </summary>
public partial class Map
{
    // .v1 suffix to match the convention spelled out in
    // AppSettingsService.InitializeAsync ("any new key MUST follow
    // <camelCaseName>.vN"). The earlier unversioned key
    // "hints.mapLongPress.dismissed" is ignored on load so existing
    // helms see the coachmark once more after upgrade -- acceptable
    // for a one-shot tutorial; bumps the version every revision so
    // future copy changes can re-trigger without a schema migration.
    private const string TouchCoachmarkKey = "hints.mapLongPress.v1.dismissed";
    private bool touchCoachmarkVisible;
    private System.Threading.Timer? touchCoachmarkTimer;

    // Versioned KV key so a future welcome revision can re-trigger the
    // card ("welcome.v2.dismissed") without a schema migration.
    private const string WelcomeKey = "hints.welcome.v1.dismissed";
    private bool welcomeVisible;

    /// <summary>
    /// Surface the welcome card AT INIT time, before the long
    /// chain of REST seeds + JS interop init in OnAfterRenderAsync
    /// runs. Earlier the card waited until after those completed,
    /// which meant any "Couldn't reach SignalK" toast painted on
    /// top of the welcome card during the same tick -- helms saw
    /// the card half-covered by the error and tapped Got It without
    /// reading. Pushing this into OnInitializedAsync lets the card
    /// land first and the toasts queue under it.
    ///
    /// The OnAfterRenderAsync gate that previously called this
    /// helper now just reads the existing <see cref="welcomeVisible"/>
    /// to decide whether to chain the touch coachmark.
    /// </summary>
    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();
        // ?welcome=1 (set by the Settings page's "Show welcome card
        // again" link) bypasses the dismissed flag without clearing
        // it. ApplyMapDeepLinkAsync drops the query after consume so
        // a page reload doesn't loop (welcome is one of the recognised
        // deep-link keys it strips). The parsing path is tested in
        // MapWelcomeQueryTests via the pure helper below.
        bool forceShow = ShouldForceShowFromUri(Nav.Uri);
        await MaybeShowWelcomeAsync(forceShow: forceShow);
    }

    /// <summary>
    /// Pure helper: returns true iff the URI carries an explicit
    /// <c>welcome=1</c> query parameter (parsed, not substring-matched).
    /// Earlier code used <c>Contains("welcome=1")</c> which over-matched
    /// on URLs like <c>/map?other=welcome=1abc</c> -- realistic when
    /// a chart link or a forwarded URL accumulates query keys. The
    /// parsed approach also gates on the value being literal "1" so
    /// <c>?welcome=true</c> or <c>?welcome=yes</c> doesn't trigger.
    /// Internal-static for direct unit testing.
    /// </summary>
    public static bool ShouldForceShowFromUri(string? uriString)
    {
        if (string.IsNullOrEmpty(uriString)) return false;
        Uri uri;
        try { uri = new Uri(uriString); }
        catch { return false; }
        if (string.IsNullOrEmpty(uri.Query)) return false;
        // Hand-roll the parse rather than reach for
        // Microsoft.AspNetCore.WebUtilities (not in the WASM bundle's
        // dependency closure -- adding it would inflate cold-start
        // download by tens of KB for one query check). The parse rule:
        // strip the leading '?' if present, split on '&', for each
        // chunk split on '=' once, and look for an exact key=value
        // match of welcome=1. Multiple welcome= entries (?welcome=1
        // &welcome=1) intentionally fail the "one value" gate so a
        // copy-paste-doubled URL doesn't silently still trigger.
        ReadOnlySpan<char> q = uri.Query.AsSpan();
        if (q.Length > 0 && q[0] == '?') q = q[1..];
        int matches = 0;
        bool valueIsOne = true;
        foreach (var range in q.Split('&'))
        {
            var kv = q[range];
            if (kv.IsEmpty) continue;
            int eq = kv.IndexOf('=');
            if (eq < 0) continue;
            var key = kv[..eq];
            var val = kv[(eq + 1)..];
            if (!key.SequenceEqual("welcome")) continue;
            matches++;
            if (!val.SequenceEqual("1")) valueIsOne = false;
        }
        return matches == 1 && valueIsOne;
    }

    /// <summary>
    /// Returns true if the welcome card was shown so OnAfterRenderAsync
    /// can gate the coachmark: both on the same first visit piles up,
    /// so we show welcome now and let DismissWelcome chain the
    /// coachmark when the user taps Got it.
    /// <para>
    /// <paramref name="forceShow"/> bypasses the KV "dismissed" flag
    /// without clearing it -- the Settings page's "Show welcome again"
    /// link uses this via the ?welcome=1 deep link so the helm can
    /// re-read the onboarding without losing the persisted dismissed
    /// state.
    /// </para>
    /// </summary>
    private async Task<bool> MaybeShowWelcomeAsync(bool forceShow = false)
    {
        try
        {
            if (!forceShow)
            {
                var dismissed = await Kv.GetAsync(WelcomeKey);
                if (dismissed == "1") return false;
            }
            welcomeVisible = true;
            StateHasChanged();
            return true;
        }
        catch (Exception) { return false; }
    }

    private async Task DismissWelcome()
    {
        if (!welcomeVisible) return;
        welcomeVisible = false;
        try { await Kv.SetAsync(WelcomeKey, "1"); } catch { }
        StateHasChanged();
        // After dismissing the welcome card, chain the touch coachmark so
        // first-run users still learn about long-press. Without this the
        // coachmark was suppressed (blocked on first visit), then never
        // surfaced on session 2 for users who don't return quickly.
        await MaybeShowTouchCoachmarkAsync();
    }

    private async Task MaybeShowTouchCoachmarkAsync()
    {
        try
        {
            var dismissed = await Kv.GetAsync(TouchCoachmarkKey);
            if (dismissed == "1") return;
            touchCoachmarkVisible = true;
            StateHasChanged();
            // Auto-dismiss after a generous window so someone glancing
            // up from a chart plot still catches it.
            touchCoachmarkTimer = new System.Threading.Timer(
                _ => _ = InvokeAsync(async () => await DismissTouchCoachmark()),
                null, 6_000, Timeout.Infinite);
        }
        catch (Exception) { /* KV unavailable in some test hosts; degrade silently. */ }
    }

    private async Task DismissTouchCoachmark()
    {
        if (!touchCoachmarkVisible) return;
        touchCoachmarkVisible = false;
        touchCoachmarkTimer?.Dispose();
        touchCoachmarkTimer = null;
        try { await Kv.SetAsync(TouchCoachmarkKey, "1"); } catch { }
        StateHasChanged();
    }
}
