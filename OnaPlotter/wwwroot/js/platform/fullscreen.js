// Document-level fullscreen toggle. The topbar button drives this; the
// map used to own its own Leaflet control but that's been removed so a
// single app-level toggle covers every page consistently.
//
// iPad Safari caveat: the Fullscreen API only works for <video> (and
// sometimes <iframe>) elements on iOS. Calling requestFullscreen() on
// document.documentElement silently fails / no-ops, and subsequent
// fullscreenchange events never fire - so isFullscreen() stays false
// and the topbar toggle is stuck "off" even though the user perceives
// a fullscreen-ish state. We detect iOS and fall back to a CSS class
// on <html> (.ios-fullbleed) that hides browser chrome-adjacent UI
// via styling alone. A home-screen-installed PWA is the real
// fullscreen path on iPad - see manifest.json display: fullscreen.

// Module-level refs hoisted to the top so unsubscribe() reads them
// without ESLint's no-use-before-define firing. _attachedBtn /
// _nativeHandler hold the topbar button + its native click handler;
// attachTrigger() populates them, unsubscribe() releases them on
// page teardown.
let dotnetRef = null;
let _attachedBtn = null;
let _nativeHandler = null;

function isIos() {
    // iPadOS 13+ fakes "MacIntel" in platform; multi-touch is the tell.
    const ua = navigator.userAgent;
    return /iPad|iPhone|iPod/.test(ua)
        || (navigator.maxTouchPoints > 1 && /Mac/.test(navigator.platform));
}

function isStandalone() {
    // Home-screen installed PWA - already "fullscreen" from the user's
    // point of view. The topbar toggle should reflect that so we don't
    // prompt users to enter a fullscreen they're already in.
    try {
        return window.matchMedia('(display-mode: fullscreen), (display-mode: standalone)').matches
            || window.navigator.standalone === true;
    } catch { return false; }
}

function apiFullscreen() {
    return !!(document.fullscreenElement || document.webkitFullscreenElement);
}

function isFullscreen() {
    if (isIos()) {
        return isStandalone() || document.documentElement.classList.contains('ios-fullbleed');
    }
    return apiFullscreen();
}

function notify() {
    if (dotnetRef) {
        try { dotnetRef.invokeMethodAsync('OnFullscreenChanged', isFullscreen()); }
        catch { /* Blazor circuit gone; no-op. */ }
    }
}

export function subscribe(ref) {
    dotnetRef = ref;
    // Both event names for the standard + webkit-prefixed Fullscreen
    // API. iOS won't fire either for our case but listening costs
    // nothing and keeps the non-iOS path identical.
    document.addEventListener('fullscreenchange', notify);
    document.addEventListener('webkitfullscreenchange', notify);
}

export function unsubscribe() {
    document.removeEventListener('fullscreenchange', notify);
    document.removeEventListener('webkitfullscreenchange', notify);
    if (_attachedBtn && _nativeHandler) {
        _attachedBtn.removeEventListener('click', _nativeHandler);
    }
    _attachedBtn = null;
    _nativeHandler = null;
    dotnetRef = null;
}

// Core toggle logic. Called either from the native click listener
// installed by attachTrigger() (synchronous, preserves user gesture)
// or from the legacy C# ToggleFullscreen() for tests / non-button
// invocations. The Safari-specific requirement is the gesture chain,
// not the function itself.
export function toggle() {
    if (isIos()) {
        // CSS full-bleed fallback. Toggle the class on <html> and
        // notify manually since no fullscreenchange event will fire.
        document.documentElement.classList.toggle('ios-fullbleed');
        notify();
        return;
    }
    if (apiFullscreen()) {
        const exit = document.exitFullscreen || document.webkitExitFullscreen;
        if (exit) {
            try {
                const p = exit.call(document);
                if (p && p.catch) p.catch(() => { });
            } catch { /* ignore */ }
        }
    } else {
        const el = document.documentElement;
        const req = el.requestFullscreen || el.webkitRequestFullscreen;
        if (req) {
            try {
                const p = req.call(el);
                if (p && p.catch) p.catch(() => { });
            } catch { /* ignore */ }
        }
    }
}

// Attach a native click listener to the topbar fullscreen button.
// This is what makes requestFullscreen actually work on Safari /
// Chrome: the call needs to be synchronous with the user gesture,
// and Blazor's @onclick dispatcher crosses enough async boundaries
// to lose that context. A vanilla addEventListener inside the button
// keeps the gesture chain intact. _attachedBtn / _nativeHandler are
// declared at the top of the file (see hoisting block above).
export function attachTrigger(btn) {
    if (!btn || btn === _attachedBtn) return;
    if (_attachedBtn && _nativeHandler) {
        _attachedBtn.removeEventListener('click', _nativeHandler);
    }
    _attachedBtn = btn;
    _nativeHandler = (e) => {
        // Synchronous; user gesture intact.
        e.preventDefault();
        toggle();
    };
    btn.addEventListener('click', _nativeHandler);
}

export function getState() {
    return isFullscreen();
}

// True when tapping the button will produce a visible change.
//
// Three cases:
//   1. Standalone PWA - already fullscreen at launch, button is
//      noise. Return false so it's hidden.
//   2. iPad Safari tab - requestFullscreen on the document root
//      silently rejects, BUT toggle() falls back to a CSS class
//      (.ios-fullbleed) that hides the topbar and narrows the
//      sidebar. That's a real "give me more chart" affordance,
//      worth showing the button for. Return true.
//   3. Desktop / Android Chromium / Firefox - standard
//      Fullscreen API works. Return document.fullscreenEnabled.
export function isSupported() {
    if (isStandalone()) return false;
    if (isIos()) return true;       // CSS fallback in toggle() is meaningful
    return !!document.fullscreenEnabled;
}
