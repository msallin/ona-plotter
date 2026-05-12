// Map-page deadman tracker: stamps the "user is still here" timestamp
// from pure JS instead of routing every pointerdown / keydown through
// Blazor's @on... attribute handlers.
//
// The Razor handler version sent every event across the JSInterop
// boundary into WASM; a 41 s DevTools trace under 4x CPU throttle
// showed 1.67 s spent inside blazor.webassembly.js's onGlobalEvent
// (107 marshalled events @ ~16 ms each). The alarm rule only needs
// minute-resolution, so we coalesce locally and push to .NET once
// per quiet window - tested values: ~5 s gives <1 % of the original
// marshalling cost and is still well below any alarm threshold.
//
// Scope: document-level. While the Map page is mounted, every click /
// tap / key on the page is by definition inside the map's DOM, which
// matches the original .map-container handler's reach. The listener
// is registered on attach (OnAfterRenderAsync) and removed on detach
// (DisposeAsync), so other pages don't see it.

let _detach = null;

const DEFAULT_COALESCE_MS = 5000;

/**
 * Attach the document-level deadman listener. Idempotent: a second
 * attach without a matching detach is a no-op (covers a duplicate
 * OnAfterRenderAsync edge case).
 *
 * @param dotNetRef    DotNetObjectReference<Map> created by the page.
 * @param methodName   [JSInvokable] method to invoke (e.g.
 *                     "OnDeadmanTouch") that calls DeadmanTracker.Touch().
 * @param coalesceMs   Minimum interval between upcalls (ms). Defaults
 *                     to 5000. Lower = more precise tracking; higher =
 *                     fewer WASM round-trips. The alarm runs on a
 *                     3 s tick and fires after minutes, so 5 s is the
 *                     sweet spot.
 */
export function attachDeadman(dotNetRef, methodName, coalesceMs) {
    if (_detach) return;
    const interval = Number.isFinite(coalesceMs) && coalesceMs > 0
        ? coalesceMs : DEFAULT_COALESCE_MS;

    let pendingTimer = null;
    const fire = () => {
        pendingTimer = null;
        // catch silently: dispose can race with a queued upcall and
        // surface here as a rejected promise.
        dotNetRef.invokeMethodAsync(methodName).catch(() => {});
    };
    const onInput = () => {
        // Trailing-edge throttle: first event after a quiet period
        // schedules an upcall in `interval` ms; subsequent events
        // inside the window are absorbed (the eventual fire stamps
        // "now" on the C# side, which is at most `interval` ms ahead
        // of the most recent real event - precise enough for a
        // minute-resolution alarm).
        if (pendingTimer) return;
        pendingTimer = setTimeout(fire, interval);
    };

    // passive: true everywhere - we don't preventDefault. Capture
    // phase is unnecessary: even if a deeper handler stops
    // propagation, the original Razor @on... handler had the same
    // gap, so parity is preserved.
    document.addEventListener('pointerdown', onInput, { passive: true });
    document.addEventListener('keydown', onInput, { passive: true });

    _detach = () => {
        document.removeEventListener('pointerdown', onInput);
        document.removeEventListener('keydown', onInput);
        if (pendingTimer) { clearTimeout(pendingTimer); pendingTimer = null; }
        _detach = null;
    };

    // One upfront upcall: if the helm navigated away from the Map
    // page and returned, the tracker singleton on the C# side still
    // holds the pre-navigation timestamp. Stamping on attach
    // re-seeds it so the alarm doesn't fire instantly on return.
    dotNetRef.invokeMethodAsync(methodName).catch(() => {});
}

/**
 * Tear down the listener and cancel any pending coalesced upcall.
 * Safe to call twice. Called from the page's DisposeAsync before
 * the dotNetRef is disposed - otherwise a queued upcall would race
 * the dispose and log a Blazor invocation error.
 */
export function detachDeadman() {
    if (_detach) _detach();
}
