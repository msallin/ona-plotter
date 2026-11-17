// Tiny helpers shared by the resource-CRUD layers (waypoint, note,
// region) and the route-popup wiring in leafletInterop. Kept in one
// file so a tweak to "what does the confirm-tap workflow look like"
// changes one place.

/** HTML-escape an untrusted string for inline insertion into a popup
 *  template literal. Safe in BOTH text-node contexts (`<span>${esc(x)}</span>`)
 *  and double- or single-quoted attribute contexts (`data-nm="${esc(x)}"`).
 *
 *  The textContent -> innerHTML round-trip alone follows the HTML5 text-
 *  serialization spec, which only escapes `&`, `<`, `>`. That's safe for
 *  text nodes but NOT for attribute values: a vessel name from the SK
 *  delta containing a `"` (legitimate per ITU-R M.1371 6-bit ASCII - it
 *  fits the 64-char alphabet) would close the attribute and let the
 *  trailing string land in the DOM as new attributes. We additionally
 *  escape `"` and `'` so attr="..." and attr='...' are both safe to drop
 *  esc()'s output into. The extra escapes are inert in text-node contexts
 *  (`&quot;` / `&#39;` render as `"` / `'` to the user). */
const _escMap = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' };
const _escRe = /[&<>"']/g;
export function esc(s) {
    if (s == null) return '';
    // Pure regex replace - no DOM round-trip. A <div>.textContent based
    // escape would cost a document.createElement allocation + a DOM
    // text-serialization pass per call, and there are ~10 esc() calls
    // per AIS popup build. This shape is monomorphic
    // (one string in, one string out), stays in V8 string-internalised
    // territory, and escapes exactly the five HTML-significant chars.
    return String(s).replace(_escRe, ch => _escMap[ch]);
}

/**
 * Two-step confirm wiring for a popup's delete button. First click
 * swaps the label to "Really?" (intentionally short so the button's
 * pixel width stays close to the original "Delete" label and the
 * surrounding popup layout doesn't reflow under the helm's finger);
 * second click within 3 seconds triggers the actual server delete
 * via the C# [JSInvokable] method. A passing tap in rough weather
 * is the nightmare case; the confirm-and-timeout pattern matches
 * how native iOS/Android apps guard destructive actions without
 * pulling up a full confirm dialog.
 *
 * @param popup        Leaflet popup instance (passed by popupopen ev.popup)
 * @param selector     CSS selector for the delete button inside the popup
 * @param dotNetMethod The [JSInvokable] method name to call on confirm
 * @param id           Resource id passed to the JSInvokable
 * @param getDotNetRef Closure returning the cached dotNetRef
 *                     (read each invocation so a disposed page is a
 *                     no-op rather than a stale-ref crash).
 */
export function wireDeleteConfirm(popup, selector, dotNetMethod, id, getDotNetRef) {
    const el = popup.getElement();
    if (!el) return;
    const btn = el.querySelector(selector);
    if (!btn || btn._wired) return;
    btn._wired = true;
    const originalLabel = btn.textContent;
    let confirmTimer = null;
    const reset = () => {
        btn.classList.remove('confirming');
        btn.textContent = originalLabel;
        if (confirmTimer) { clearTimeout(confirmTimer); confirmTimer = null; }
    };
    btn.addEventListener('click', () => {
        if (!btn.classList.contains('confirming')) {
            btn.classList.add('confirming');
            btn.textContent = 'Really?';
            confirmTimer = setTimeout(reset, 3000);
            return;
        }
        reset();
        const dotNetRef = getDotNetRef();
        if (dotNetRef) dotNetRef.invokeMethodAsync(dotNetMethod, id).catch(() => {});
    });
    // Closing the popup resets confirm state so re-opening starts fresh.
    popup.once('popupclose', reset);
}

/**
 * Wire a popup button to a [JSInvokable] method. Single tap, no
 * confirm (use wireDeleteConfirm for destructive actions). A rejected
 * invoke (disposed ref, navigation in flight) is swallowed.
 *
 * @param popup        Leaflet popup instance
 * @param selector     CSS selector for the button inside the popup
 * @param getDotNetRef Closure returning the current dotNetRef
 * @param dotNetMethod The [JSInvokable] method name
 * @param args         Arguments passed to the method
 * @param opts         { close: 'before' | 'after' } closes the popup's
 *                     source popup around the invoke;
 *                     { swallow: true } prevents default and stops
 *                     propagation of the click.
 */
export function wireClick(popup, selector, getDotNetRef, dotNetMethod, args = [], opts = {}) {
    const el = popup.getElement();
    if (!el) return;
    const btn = el.querySelector(selector);
    if (!btn || btn._wired) return;
    btn._wired = true;
    btn.addEventListener('click', async (e) => {
        if (opts.swallow) { e.preventDefault(); e.stopPropagation(); }
        if (opts.close === 'before') popup._source?.closePopup();
        const dotNetRef = getDotNetRef();
        if (dotNetRef) {
            try { await dotNetRef.invokeMethodAsync(dotNetMethod, ...args); }
            catch (_) { /* disposed or navigation in flight */ }
        }
        if (opts.close === 'after') popup._source?.closePopup();
    });
}
