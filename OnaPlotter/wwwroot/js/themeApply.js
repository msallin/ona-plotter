// Apply night-mode classes to <html> instead of just .page. The
// problem: html/body reads `background-color: var(--sk-bg)` at the
// root's inherited scope, so a .page-level override doesn't reach
// them. Everything inside .page shifts to the night palette, but
// the body backdrop and any chrome outside .page stay day-coloured.
// Putting the classes on <html> lets every descendant (including
// body) see the redefined CSS tokens.
//
// Called from MainLayout on toggle and on settings-change.

export function setNightMode(enabled, preset) {
    const html = document.documentElement;
    // Drop every preset class, then add back the one we want. The
    // preset name is untrusted input -- clamp to the known set to
    // prevent attribute-value injection via Settings storage.
    const known = ['soft', 'amber', 'red'];
    for (const p of known) html.classList.remove('night-mode-' + p);
    html.classList.toggle('night-mode-global', !!enabled);
    if (enabled) {
        const safe = known.includes(preset) ? preset : 'soft';
        html.classList.add('night-mode-' + safe);
    }
}
