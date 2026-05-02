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
    private const string TouchCoachmarkKey = "hints.mapLongPress.dismissed";
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
        // it. The deep-link handler in OnAfterRenderAsync drops the
        // query string after consume so a page reload doesn't loop.
        bool forceShow = false;
        try
        {
            var uri = new Uri(Nav.Uri);
            if (!string.IsNullOrEmpty(uri.Query)
                && uri.Query.Contains("welcome=1", StringComparison.Ordinal))
            {
                forceShow = true;
            }
        }
        catch { /* malformed URI: just don't force */ }
        await MaybeShowWelcomeAsync(forceShow: forceShow);
    }

    /// <summary>
    /// Returns true if the welcome card was shown so OnAfterRenderAsync
    /// can gate the coachmark: both on the same first visit piles up,
    /// so we show welcome now and let DismissWelcome chain the
    /// coachmark when the user taps Got it.
    ///
    /// Also wired to a "Show welcome again" link in Settings so the
    /// helm can re-read the onboarding after the first dismissal --
    /// passing <paramref name="forceShow"/> = true bypasses the KV
    /// flag without clearing it.
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

    /// <summary>Public re-open hook for the Settings page's "Show
    /// welcome again" link. Surfaces the card unconditionally
    /// (regardless of the dismissed KV flag) so the helm can
    /// re-read the onboarding without nuking the flag itself --
    /// taps to Got It still close the card; the flag stays as it
    /// was on entry.</summary>
    public Task ShowWelcomeAgainAsync() => MaybeShowWelcomeAsync(forceShow: true);

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
