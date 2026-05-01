// Screen Wake Lock — standalone from leafletInterop so it can be held
// across the whole app, not only the Map page. Used to be inside
// leafletInterop.js which meant the lock released every time the user
// navigated to Settings / Wind / History; defeats the "don't let the
// helm's tablet sleep mid-watch" intent.
//
// Browser support: Chrome / Edge / Safari 16.4+. iOS Safari releases
// the lock when the tab is hidden (switching apps, background); the
// visibilitychange handler below re-acquires on return. Nothing to
// clean up when the feature isn't available -- _acquireWakeLock
// bails silently.

let _sentinel = null;
let _wanted = false;
let _visibilityHandlerInstalled = false;

async function _acquire() {
    if (!('wakeLock' in navigator)) return;
    if (_sentinel) return;
    try {
        _sentinel = await navigator.wakeLock.request('screen');
        // The 'release' event fires both on manual release and on
        // OS-initiated drop (iOS backgrounding). Null the sentinel
        // either way so a subsequent _acquire() can re-claim.
        _sentinel.addEventListener('release', () => { _sentinel = null; });
    } catch {
        // Permission denied, battery-saver, etc. -- no retry;
        // nothing here short of bothering the user.
        _sentinel = null;
    }
}

function _release() {
    const s = _sentinel;
    _sentinel = null;
    if (s) { try { s.release(); } catch { /* already released */ } }
}

/**
 * Public API. `on=true` acquires the lock (if the browser supports it
 * and the user has a foreground tab); `on=false` releases. Idempotent.
 */
export async function setWakeLock(on) {
    _wanted = !!on;
    if (_wanted) {
        await _acquire();
        if (!_visibilityHandlerInstalled) {
            _visibilityHandlerInstalled = true;
            document.addEventListener('visibilitychange', () => {
                if (_wanted && document.visibilityState === 'visible') _acquire();
            });
        }
    } else {
        _release();
    }
}
