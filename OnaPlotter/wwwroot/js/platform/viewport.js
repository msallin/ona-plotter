// Small DOM helpers for MainLayout: viewport shape and the alarm-log
// dialog's focus trap.
//
// 600 px is the same breakpoint as Bootstrap's `sm` and matches the
// existing @media (max-width: 600px) responsive rules in app.css,
// so the JS-side and CSS-side notions of "mobile" stay in sync.

export function isMobile() {
    try {
        return window.matchMedia('(max-width: 600px)').matches;
    } catch {
        return false;
    }
}

const FOCUSABLE = 'button,[href],input,select,textarea,[tabindex]:not([tabindex="-1"])';

// Which element Tab / Shift+Tab should wrap to, or null to let the
// browser move focus normally. Wraps at the ends of the list and pulls
// focus back in when it currently sits outside the list.
export function focusWrapTarget(focusables, active, back) {
    const n = focusables.length;
    if (n === 0) return null;
    const first = focusables[0];
    const last = focusables[n - 1];
    const inside = focusables.includes(active);
    if (!inside) return back ? last : first;
    if (back && active === first) return last;
    if (!back && active === last) return first;
    return null;
}

const trapped = new WeakSet();

// Keeps Tab / Shift+Tab inside `panel` (an aria-modal dialog; browsers
// don't trap focus for that attribute). Runs as a native keydown
// listener because only a synchronous handler can preventDefault the
// browser's own Tab move; a Blazor round-trip arrives after focus has
// already left. Idempotent per element; the listener goes away with the
// element when the dialog is removed from the DOM.
export function attachFocusTrap(panel, doc = globalThis.document) {
    if (!panel || trapped.has(panel)) return;
    trapped.add(panel);
    panel.addEventListener('keydown', ev => {
        if (ev.key !== 'Tab') return;
        const focusables = Array.from(panel.querySelectorAll(FOCUSABLE));
        const target = focusWrapTarget(focusables, doc.activeElement, ev.shiftKey);
        if (target === null) return;
        ev.preventDefault();
        target.focus();
    });
}
