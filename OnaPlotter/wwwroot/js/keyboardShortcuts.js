/**
 * Pure filters for the chart's global keydown handler (leafletInterop
 * enableKeyboardShortcuts). Kept free of Leaflet so node can test them.
 */

/**
 * Whether a keydown target is somewhere the helm is typing or picking a
 * value, so single-letter shortcuts must stay with the element.
 */
export function isTypingTarget(target) {
    if (!target) return false;
    const tag = target.tagName;
    if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return true;
    return !!target.isContentEditable;
}

/**
 * Lower-cased key for a keydown, or null when the event carries none.
 * Autofill and some IME / virtual-keyboard paths dispatch keydown with
 * an undefined or empty `key`.
 *
 * Example: { key: 'F' } -> 'f'; { key: undefined } -> null
 */
export function shortcutKey(e) {
    return typeof e.key === 'string' && e.key.length > 0 ? e.key.toLowerCase() : null;
}
