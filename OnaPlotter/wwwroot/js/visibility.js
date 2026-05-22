// Tab-visibility hooks.
//
// `attachVisibilityListener` is the one-shot visible-edge hook used by
// MainLayout's auth chip: when the helm switches away to the SK admin
// tab to log in and switches back, we re-probe /skServer/loginStatus
// immediately so the "Not logged in" chip clears without waiting up to
// 5 minutes for the next poll.
//
// `attachVisibilityEdges` is the both-edges hook used by Map.razor to
// pause the AIS push timer while hidden. A long-hidden tab otherwise
// queues 3 s ticks at heavy throttle; on resume Chrome unleashes the
// backlog plus a 200+ vessel CPA/COLREGS compute through the single
// WASM thread, blocks the renderer past 5 s, and trips Chrome's "page
// unresponsive" dialog.
//
// Browser support: visibilitychange is universal in modern browsers
// (every browser shipping in 2024+). No polyfill needed.

let _attached = false;

/**
 * Register a one-time document listener that fires the named
 * Blazor [JSInvokable] every time the tab becomes visible. The
 * listener is process-global - attaching twice is a no-op so a
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
        // fire on the show edge - firing on hide would just spam
        // the auth probe right before the helm leaves the tab.
        if (document.visibilityState !== 'visible') return;
        if (!dotNetRef) return;
        // catch silently: page teardown / dotnet disposal both
        // surface here as a rejected promise.
        dotNetRef.invokeMethodAsync(methodName).catch(() => {});
    });
}

// State for attachVisibilityEdges. Separate from the auth-chip flag
// above because the page-edges hook follows Map.razor's lifecycle and
// needs detach on nav-away, while the auth chip listener stays for
// the life of the app (MainLayout never unmounts).
let _edgesListener = null;

/**
 * Register a document listener that fires one of two [JSInvokable]
 * methods on every visibility transition, depending on direction.
 * One subscriber at a time - calling attach again replaces the
 * previous handler so an OnAfterRenderAsync re-run (re-mount on
 * browser back / forward) doesn't stack listeners.
 *
 * Also fires the hidden-edge callback synchronously if the tab is
 * already hidden at attach time - covers the case where the helm
 * navigated to /map from another tab and switched away before the
 * page finished mounting; without this, the AIS timer the page just
 * armed would tick uselessly until the helm returns and the visible-
 * edge fires.
 *
 * @param dotNetRef        DotNetObjectReference<Map> from C# side
 * @param onVisibleMethod  [JSInvokable] name to invoke on visible-edge
 * @param onHiddenMethod   [JSInvokable] name to invoke on hidden-edge
 */
export function attachVisibilityEdges(dotNetRef, onVisibleMethod, onHiddenMethod) {
    detachVisibilityEdges();
    _edgesListener = () => {
        const method = document.visibilityState === 'visible'
            ? onVisibleMethod : onHiddenMethod;
        if (!method || !dotNetRef) return;
        dotNetRef.invokeMethodAsync(method).catch(() => {});
    };
    document.addEventListener('visibilitychange', _edgesListener);
    if (document.visibilityState === 'hidden' && onHiddenMethod && dotNetRef) {
        dotNetRef.invokeMethodAsync(onHiddenMethod).catch(() => {});
    }
}

/**
 * Tear down the listener set up by attachVisibilityEdges. Safe to
 * call when no listener is attached (idempotent).
 */
export function detachVisibilityEdges() {
    if (!_edgesListener) return;
    document.removeEventListener('visibilitychange', _edgesListener);
    _edgesListener = null;
}
