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
 * Firefox -- shorter values race the click event on slow tablets.
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
