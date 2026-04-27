// Apply theme + night-mode classes to <html> instead of just .page.
// The problem: html/body reads `background-color: var(--sk-bg)` at the
// root's inherited scope, so a .page-level override doesn't reach
// them. Everything inside .page shifts to the new palette, but the
// body backdrop and any chrome outside .page stay default-coloured.
// Putting the classes on <html> lets every descendant (including
// body) see the redefined CSS tokens, which is the difference
// between a usable light theme and one where every page-level h1/h2
// renders dark-on-dark because the body never repainted.
//
// Both functions are called from MainLayout on toggle and on
// settings-change.

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

export function setTheme(theme) {
    const html = document.documentElement;
    // Whitelist mirrors IAppSettings.Theme normalisation. Dropping any
    // unknown value to "system" keeps storage corruption from putting
    // the document into a class-injection state.
    const known = ['light', 'dark', 'system', 'high-contrast'];
    for (const t of known) html.classList.remove('theme-' + t);
    const safe = known.includes(theme) ? theme : 'system';
    html.classList.add('theme-' + safe);
}
