// Document-level fullscreen toggle. The topbar button drives this; the
// map used to own its own Leaflet control but that's been removed so a
// single app-level toggle covers every page consistently.

let dotnetRef = null;

function isFullscreen() {
    return !!(document.fullscreenElement || document.webkitFullscreenElement);
}

function notify() {
    if (dotnetRef) {
        try { dotnetRef.invokeMethodAsync('OnFullscreenChanged', isFullscreen()); }
        catch { /* Blazor circuit gone; no-op. */ }
    }
}

export function subscribe(ref) {
    dotnetRef = ref;
    document.addEventListener('fullscreenchange', notify);
    document.addEventListener('webkitfullscreenchange', notify);
}

export function unsubscribe() {
    document.removeEventListener('fullscreenchange', notify);
    document.removeEventListener('webkitfullscreenchange', notify);
    dotnetRef = null;
}

export function toggle() {
    if (isFullscreen()) {
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
