// Tiny shared module for "let the helm save / open a file from
// in-app code". Used by Settings (GPX import / export) so the
// Settings page doesn't have to pull in the whole leafletInterop
// module for one download helper.
//
// leafletInterop.js still has its own copy because the Map page
// uses it from a context where the leaflet module is already
// loaded; deduping the two copies is a future refactor.

/**
 * Triggers a browser download of `content` (string or Blob) under the
 * given filename. Uses a Blob + temporary anchor + click pattern that
 * works in every modern browser without prompting for permission and
 * without leaving the URL alive longer than the click handler. The
 * 100 ms revoke delay is the safe minimum across Chromium / WebKit /
 * Firefox - shorter values race the click event on slow tablets.
 */
export function triggerFileDownload(filename, content) {
    const blob = (content instanceof Blob)
        ? content
        : new Blob([content], { type: 'application/octet-stream' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    setTimeout(() => { URL.revokeObjectURL(url); a.remove(); }, 100);
}

/**
 * Share text via the browser's Web Share API, falling back to a
 * clipboard copy when the API is unavailable or the helm cancels
 * the system sheet. Returns the actual outcome so the C# caller
 * can render the right toast: "Shared" vs "Copied to clipboard"
 * vs "Couldn't share".
 *
 * Web Share API support, as of 2026-04: iOS Safari 12.2+, Android
 * Chrome 89+, Edge / Chrome on Win10+. Firefox + desktop Safari
 * lack it -> the helm gets the clipboard fallback. Native share
 * sheet on iPad / Android lists WhatsApp / iMessage / Mail / etc;
 * one button covers them all without a per-target link.
 *
 * Returns the string outcome:
 *   'shared'       - Web Share completed (system sheet picked + sent)
 *   'cancelled'    - helm tapped Cancel on the share sheet
 *   'copied'       - fallback path; text is now on the clipboard
 *   'unavailable'  - neither share nor clipboard worked
 */
export async function shareOrCopy(title, text) {
    // Web Share first. AbortError = the helm cancelled the sheet
    // (still a valid outcome - they saw the sheet, decided not to
    // send). Other errors = the API barfed; fall through to clipboard.
    if (typeof navigator.share === 'function') {
        try {
            await navigator.share({ title, text });
            return 'shared';
        } catch (err) {
            if (err && err.name === 'AbortError') return 'cancelled';
            // Anything else (NotAllowedError on insecure context,
            // DataError on bad payload, etc.) - try clipboard.
        }
    }
    // Clipboard API requires a secure context (https / localhost)
    // AND, on some browsers, a user gesture. Wrap defensively;
    // a thrown SecurityError downgrades to 'unavailable' so the
    // C# caller can show "couldn't share".
    if (navigator.clipboard && typeof navigator.clipboard.writeText === 'function') {
        try {
            await navigator.clipboard.writeText(text);
            return 'copied';
        } catch { /* fall through */ }
    }
    return 'unavailable';
}
