// Synchronous bootstrap of the client-error relay. Loaded as a
// regular (non-module) <script> in index.html BEFORE
// blazor.webassembly.js so the listeners and console.error wrapper
// are armed before the Blazor runtime starts catching its own
// exceptions. Exposes window.__onaErrorRelay.report for any explicit
// relay calls (the C# ClientErrorRelay service uses it).
//
// Why synchronous inline-style rather than a deferred module:
//   - Blazor WASM logs unhandled component exceptions via
//     console.error, then catches them so window.onerror never
//     fires. If our wrapper isn't installed BEFORE the first
//     console.error fires, that error vanishes from the relay
//     and the helm sees the banner with nothing on the server.
//   - ES module imports run after DOM-ready, often after the first
//     Blazor render tick. Too late for boot-time exceptions.
//
// Self-installs on first execution; the install guard turns
// subsequent loads (e.g. via service worker re-fetch) into no-ops.

(function () {
    if (window.__onaErrorRelay) return; // already installed

    // ---- Constants ----------------------------------------------------
    // 200 ms is tight enough to throttle a render-error loop (~5/sec
    // cap) but loose enough that the console.error and follow-up
    // banner signal for the SAME error both make it to the server.
    var MIN_INTERVAL_MS = 200;
    // Caps on the wire payload so a misbehaving client can't flood
    // the SignalK log with a megabyte of minified stack. The server
    // also caps at 8 KB total per entry; these match.
    var MAX_MESSAGE_CHARS = 800;
    var MAX_STACK_CHARS = 8000;
    var MAX_BANNER_CHARS = 2000;

    var lastSendMs = 0;
    var relayUrl = null;
    // Re-entrancy guard: any path inside the wrapper that ends up
    // triggering another console.error (very unlikely but possible
    // if a future contributor adds logging to formatErrorLike or
    // sendOnce) would otherwise crash-loop.
    var inWrapper = false;

    function resolveRelayUrl() {
        if (relayUrl) return relayUrl;
        try {
            // baseHref rather than `base` -- the latter reads as a
            // weakly-reserved word and trips up some minifiers /
            // strict-mode passes even though plain ES6 accepts it.
            var baseHref = document.baseURI || (window.location.origin + '/');
            relayUrl = new URL('log', baseHref).toString();
        } catch (e) {
            relayUrl = '/log';
        }
        return relayUrl;
    }

    function sendOnce(payload) {
        try {
            fetch(resolveRelayUrl(), {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                credentials: 'same-origin',
                body: JSON.stringify(payload),
                // keepalive lets the request finish even if the page
                // is unloading mid-crash; otherwise an onerror during
                // tab close would silently drop. Firefox needs >= 116.
                keepalive: true,
            }).catch(function () { /* best-effort */ });
        } catch (_) { /* best-effort */ }
    }

    // Returns true iff the throttle gate just opened (and updates the
    // timestamp). Callers do `if (!throttleOk()) return;` BEFORE
    // building the payload so we don't allocate strings + scan
    // arguments on every dropped event during a render-error storm.
    function throttleOk() {
        var now = Date.now();
        if (now - lastSendMs < MIN_INTERVAL_MS) return false;
        lastSendMs = now;
        return true;
    }

    function formatErrorLike(arg) {
        if (arg == null) return '';
        if (arg instanceof Error) {
            return (arg.stack || (arg.name + ': ' + arg.message)).toString();
        }
        if (typeof arg === 'string') return arg;
        try { return JSON.stringify(arg); }
        catch (_) { return String(arg); }
    }

    // window 'error': synchronous global exceptions.
    window.addEventListener('error', function (e) {
        if (!throttleOk()) return;
        var err = (e && e.error) || e;
        sendOnce({
            message: (err && err.message) || String((e && e.message) || e || 'unknown'),
            stack: (err && err.stack) || '',
            url: window.location.href,
            userAgent: navigator.userAgent || '',
            ts: new Date().toISOString(),
        });
    });

    // 'unhandledrejection': orphaned Promise rejections.
    window.addEventListener('unhandledrejection', function (e) {
        if (!throttleOk()) return;
        var r = e && e.reason;
        sendOnce({
            message: (r && (r.message || String(r))) || 'unhandledrejection',
            stack: (r && r.stack) || '',
            url: window.location.href,
            userAgent: navigator.userAgent || '',
            ts: new Date().toISOString(),
        });
    });

    // console.error wrapper: Blazor WASM catches component
    // exceptions internally and only logs via console.error before
    // showing the banner. This is the critical hook -- without it,
    // every Razor lifecycle exception vanishes from the relay.
    // The wrapper runs ONLY the throttle check on the hot path; the
    // payload-building only happens when we're actually going to send.
    // NEVER call console.error from inside the wrapper (would
    // recurse infinitely; the inWrapper guard catches that).
    // The outer `if (window.__onaErrorRelay) return;` at the top of
    // the IIFE already prevents this script from running twice in the
    // same page, so a __onaWrapped marker check on the original here
    // would be dead code; we just install the wrapper unconditionally.
    var originalConsoleError = console.error.bind(console);
    {
        var wrapped = function () {
            // Always pass through to the original first so devtools
            // see the entry even if our throttle gate drops it.
            var args = arguments;
            var passthrough = function () {
                return originalConsoleError.apply(console, args);
            };
            if (inWrapper || !throttleOk()) return passthrough();
            inWrapper = true;
            try {
                var argsArr = Array.prototype.slice.call(args);
                var err = null;
                for (var i = 0; i < argsArr.length; i++) {
                    if (argsArr[i] instanceof Error) { err = argsArr[i]; break; }
                }
                var message = err
                    ? (err.message || err.name || 'Error')
                    : argsArr.map(formatErrorLike).join(' ').slice(0, MAX_MESSAGE_CHARS);
                var stack = err
                    ? (err.stack || '')
                    : argsArr.map(formatErrorLike).join('\n').slice(0, MAX_STACK_CHARS);
                sendOnce({
                    message: 'console.error: ' + message,
                    stack: stack,
                    url: window.location.href,
                    userAgent: navigator.userAgent || '',
                    ts: new Date().toISOString(),
                });
            } catch (_) { /* never let the relay take the app down */ }
            finally { inWrapper = false; }
            return passthrough();
        };
        // displayName helps Firefox devtools show this as the wrapped
        // console.error rather than as an opaque closure -- click-
        // through to source still lands here, but at least the
        // function name in the stack reads sensibly.
        try { Object.defineProperty(wrapped, 'name', { value: 'console.error' }); } catch (_) { }
        wrapped.displayName = 'console.error';
        console.error = wrapped;
    }

    // MutationObserver on the banner: belt-and-braces fallback.
    // If a future Blazor release changes the console.error shape so
    // our extractor misses the stack, at least the timestamp + the
    // banner's own innerText reach the server.
    //
    // Reads `el.style.display` directly rather than getComputedStyle
    // so we don't trigger a synchronous style recalc on every banner
    // mutation -- Blazor toggles inline style, so the property read
    // is sufficient and free.
    function attachBannerObserver() {
        var el = document.getElementById('blazor-error-ui');
        if (!el) {
            // DOM not yet parsed; retry on DOMContentLoaded.
            document.addEventListener('DOMContentLoaded', attachBannerObserver, { once: true });
            return;
        }
        var lastVisible = false;
        var isVisible = function () {
            // Blazor sets `style="display: block"` on the banner when
            // it shows; the default empty inline style means hidden
            // (CSS .blazor-error-ui starts at display:none).
            return el.style.display && el.style.display !== 'none';
        };
        var check = function () {
            var visible = isVisible();
            if (visible && !lastVisible && throttleOk()) {
                sendOnce({
                    message: 'blazor-error-ui banner shown (see console.error entries for stack)',
                    stack: (el.innerText || '').slice(0, MAX_BANNER_CHARS),
                    url: window.location.href,
                    userAgent: navigator.userAgent || '',
                    ts: new Date().toISOString(),
                });
            }
            lastVisible = visible;
        };
        check();
        var observer = new MutationObserver(check);
        // attributeFilter narrows the wakeup to the only mutations
        // that change visibility; we don't need childList/subtree
        // for that, so they're omitted to keep the observer quiet.
        observer.observe(el, {
            attributes: true,
            attributeFilter: ['style', 'class'],
        });
    }
    attachBannerObserver();

    // Public surface for the C# ClientErrorRelay helper. Anything
    // that wants to relay a custom message from outside this file
    // goes through window.__onaErrorRelay.report({ message, stack }).
    window.__onaErrorRelay = {
        report: function (payload) {
            if (!throttleOk()) return;
            sendOnce({
                message: (payload && payload.message) || 'manual report',
                stack: (payload && payload.stack) || '',
                url: window.location.href,
                userAgent: navigator.userAgent || '',
                ts: new Date().toISOString(),
            });
        }
    };
})();
