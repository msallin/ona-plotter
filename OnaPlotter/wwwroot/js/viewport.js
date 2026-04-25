// Tiny viewport-shape helper. Currently only used by MainLayout to
// decide whether the first-run sidebar default should land in
// icon-rail mode (phone width: too small for full-width labels).
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
