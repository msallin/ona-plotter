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
    // Keep Bootstrap's data-bs-theme in lockstep so .form-check / .btn /
    // .form-control pick the right token set instead of staying in the
    // index.html bootstrap value while our --sk-* palette flipped. We
    // collapse high-contrast to "light" because Bootstrap only ships
    // two palettes; our high-contrast is a light variant with thicker
    // chrome, not a third Bootstrap palette. System resolves through
    // prefers-color-scheme so iOS dark-at-night flows match.
    let bs;
    if (safe === 'dark') bs = 'dark';
    else if (safe === 'system') {
        bs = (typeof window !== 'undefined'
            && window.matchMedia
            && window.matchMedia('(prefers-color-scheme: dark)').matches) ? 'dark' : 'light';
    }
    else bs = 'light';   // light + high-contrast
    html.setAttribute('data-bs-theme', bs);
    applyEffectiveLight(safe);
}

// Synthetic class that's true when the page should render in a light
// palette: explicit theme-light, theme-high-contrast, OR theme-system
// resolved against a light OS via prefers-color-scheme. CSS rules that
// previously needed three hand-rolled selectors (one per theme name +
// a media-query-wrapped system mirror) collapse to a single
// `:where(.theme-effective-light) X` selector. Reduces ~50 duplicated
// rules in app.css to single-source overrides; keeps the cascade and
// specificity calm.
//
// Listener: matchMedia change events propagate at runtime so a sunset
// flip on iOS / macOS automatically re-applies the class without
// needing a re-toggle from the helm. Stored on the function so a
// second setTheme() call doesn't pile listeners.
let _systemMql = null;
let _systemListener = null;
function applyEffectiveLight(safeTheme) {
    const html = document.documentElement;
    const isLightExplicit = safeTheme === 'light' || safeTheme === 'high-contrast';
    const isSystem = safeTheme === 'system';
    const updateClass = () => {
        const systemIsLight = !!(typeof window !== 'undefined'
            && window.matchMedia
            && window.matchMedia('(prefers-color-scheme: light)').matches);
        const effective = isLightExplicit || (isSystem && systemIsLight);
        html.classList.toggle('theme-effective-light', effective);
    };
    updateClass();
    // Tear down any previous listener so toggling between themes
    // doesn't pile callbacks. matchMedia change reflects an OS-level
    // prefers-color-scheme flip (sunset, manual switch, schedule).
    if (_systemMql && _systemListener) {
        _systemMql.removeEventListener('change', _systemListener);
        _systemMql = null;
        _systemListener = null;
    }
    if (isSystem && typeof window !== 'undefined' && window.matchMedia) {
        _systemMql = window.matchMedia('(prefers-color-scheme: light)');
        _systemListener = () => updateClass();
        _systemMql.addEventListener('change', _systemListener);
    }
}
