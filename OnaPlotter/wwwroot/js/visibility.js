// Tab-visibility hook used by MainLayout's auth chip: when the
// helm switches away to the SK admin tab to log in and switches
// back, we want to re-probe /skServer/loginStatus immediately so
// the "Not logged in" chip clears without waiting up to 5 minutes
// for the next poll. Without this, the chip is a UX dead-end --
// the helm fixed the underlying problem but the warning insists
// otherwise.
//
// Browser support: visibilitychange is universal in modern
// browsers (every browser shipping in 2024+). No polyfill needed.

let _attached = false;

/**
 * Register a one-time document listener that fires the named
 * Blazor [JSInvokable] every time the tab becomes visible. The
 * listener is process-global -- attaching twice is a no-op so a
 * mid-session re-attach (page navigation) doesn't double-fire.
 *
 * @param dotNetRef    DotNetObjectReference<MainLayout> from C# side
 * @param methodName   [JSInvokable] method name on MainLayout
 *                     (e.g. "OnTabVisible") to invoke on each show
 */
export function attachVisibilityListener(dotNetRef, methodName) {
    if (_attached) return;
    _attached = true;
    document.addEventListener('visibilitychange', () => {
        // visibilityState transitions: visible <-> hidden. We only
        // fire on the show edge -- firing on hide would just spam
        // the auth probe right before the helm leaves the tab.
        if (document.visibilityState !== 'visible') return;
        if (!dotNetRef) return;
        // catch silently: page teardown / dotnet disposal both
        // surface here as a rejected promise.
        dotNetRef.invokeMethodAsync(methodName).catch(() => {});
    });
}
