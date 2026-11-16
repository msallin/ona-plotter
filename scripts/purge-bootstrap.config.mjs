/**
 * PurgeCSS config that cuts Bootstrap down to the rules OnaPlotter uses.
 *
 * The full bootstrap.min.css is ~230 KB and the app touches a few
 * percent of its classes. The output, OnaPlotter/wwwroot/css/
 * bootstrap/bootstrap.min.css, is committed and loaded by index.html in
 * place of the vendor file, so the .NET build needs no Node step.
 *
 * Regenerate after adding a Bootstrap class to any .razor / .cs / .js
 * file, or after updating the vendor Bootstrap (from the repo root):
 *   npx purgecss@9.1.0 --config scripts/purge-bootstrap.config.mjs --output OnaPlotter/wwwroot/css/bootstrap
 *
 * Classes assembled at runtime from fragments (e.g. "btn-" + kind) are
 * invisible to the extractor; add a pattern to `safelist` when you
 * introduce one.
 */
export default {
    css: ['OnaPlotter/wwwroot/lib/bootstrap/dist/css/bootstrap.min.css'],
    content: [
        'OnaPlotter/**/*.razor',
        'OnaPlotter/**/*.cs',
        'OnaPlotter/wwwroot/index.html',
        'OnaPlotter/wwwroot/js/**/*.js',
    ],
    // Custom properties are cheap and Bootstrap rules reference them
    // indirectly (--bs-*), so keep every variable and keyframe.
    variables: false,
    keyframes: false,
    fontFace: false,
    safelist: {
        standard: [
            'html', 'body', ':root',
            // Focus / validation / toggle states set at runtime.
            'was-validated', 'is-invalid', 'is-valid', 'show', 'active', 'disabled',
        ],
        // Theme attribute set by index.html / themeApply.js.
        greedy: [/data-bs-theme/],
    },
};
