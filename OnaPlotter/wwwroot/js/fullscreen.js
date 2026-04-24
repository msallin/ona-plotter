// Document-level fullscreen toggle. The topbar button drives this; the
// map used to own its own Leaflet control but that's been removed so a
// single app-level toggle covers every page consistently.
//
// iPad Safari caveat: the Fullscreen API only works for <video> (and
// sometimes <iframe>) elements on iOS. Calling requestFullscreen() on
// document.documentElement silently fails / no-ops, and subsequent
// fullscreenchange events never fire -- so isFullscreen() stays false
// and the topbar toggle is stuck "off" even though the user perceives
// a fullscreen-ish state. We detect iOS and fall back to a CSS class
// on <html> (.ios-fullbleed) that hides browser chrome-adjacent UI
// via styling alone. A home-screen-installed PWA is the real
// fullscreen path on iPad -- see manifest.json display: fullscreen.

let dotnetRef = null;

function isIos() {
    // iPadOS 13+ fakes "MacIntel" in platform; multi-touch is the tell.
    const ua = navigator.userAgent;
    return /iPad|iPhone|iPod/.test(ua)
        || (navigator.maxTouchPoints > 1 && /Mac/.test(navigator.platform));
}

function isStandalone() {
    // Home-screen installed PWA -- already "fullscreen" from the user's
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
    dotnetRef = null;
}

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

export function getState() {
    return isFullscreen();
}
