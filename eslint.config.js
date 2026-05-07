// Repo-root ESLint config. Lints the hand-written JS interop layer
// under OnaPlotter/wwwroot/js so production-only crashes like the
// "can't access lexical declaration 'editModeDeps' before
// initialization" TDZ ReferenceError get caught at PR time, not on
// a freshly loaded helm tablet.
//
// Scope: just OnaPlotter/wwwroot/js. The Razor-managed WASM
// runtime, OnaPlotter.UiTests (Playwright), and node_modules are
// outside this config - they have their own toolchains.
//
// Rule philosophy: tight runtime-correctness rules, NO stylistic
// rules. The codebase is hand-formatted and consistent; turning on
// `semi`, `quotes`, `indent` etc. now would generate a thousand-
// line cosmetic diff that buries the actual signal. Things that
// are likely real bugs (use-before-define / no-undef / dupe-keys)
// stay; things that are taste calls (arrow style, comma dangle)
// stay off. Bumping the rule set later is one PR away.

import js from '@eslint/js';
import globals from 'globals';

export default [
    js.configs.recommended,
    {
        files: ['OnaPlotter/wwwroot/js/**/*.js'],
        languageOptions: {
            ecmaVersion: 2022,
            sourceType: 'module',
            globals: {
                ...globals.browser,
                // Blazor JS interop entry point. Razor-side calls land
                // through DotNet.invokeMethod / invokeMethodAsync.
                DotNet: 'readonly',
                // Leaflet is loaded as a global script tag in
                // index.html (not an npm import) so Blazor can rely on
                // window.L being live before the WASM runtime starts
                // calling JS interop. Marking it readonly here matches
                // the actual usage and silences no-undef at every
                // L.map(), L.marker() call.
                L: 'readonly',
                // Node's built-in test runner is used by *.test.js
                // siblings of the production sources. The flat config
                // can't easily split per-file globals without a second
                // block, so the test-runner globals live alongside
                // browser globals; production code that touches these
                // would still trip the runtime, so the leak is mild.
                process: 'readonly',
            },
        },
        rules: {
            // The actual rule that would have caught the editModeDeps
            // bug. functions:false keeps hoisted function declarations
            // legal (they hoist by spec); variables/classes:true
            // catches let/const/class TDZ and var-before-decl.
            // The actual rule that would have caught the editModeDeps
            // TDZ bug. Tuning:
            //   functions:false - function declarations hoist, so
            //     forward refs to `function foo()` are legitimate at
            //     runtime. Only `let`/`const`/`class` are in TDZ.
            //   classes:false - L.Layer.extend({...}) and similar
            //     class-style declarations are referenced inside
            //     deferred callbacks (Leaflet draws, mode handlers)
            //     that fire only after module init completes; TDZ
            //     can't actually trip in the patterns this codebase
            //     uses. Setting true generated 3 false positives in
            //     radarLayer.js with no real bug behind any of them.
            //   variables:true - catches the bug class. A `const X`
            //     used at module-init time before its declaration
            //     line will fire the rule here.
            'no-use-before-define': ['error', {
                functions: false,
                classes: false,
                variables: true,
            }],
            // ESLint 9 recommended already enables most of these;
            // explicit listing makes the intent visible at config-
            // review time rather than buried in a preset.
            'no-undef': 'error',
            'no-redeclare': 'error',
            'no-const-assign': 'error',
            'no-dupe-keys': 'error',
            'no-dupe-args': 'error',
            'no-unreachable': 'error',
            // Unused vars are warnings, not errors: the JS interop
            // code occasionally takes options bags from C# whose
            // shape will grow in a later PR. argsIgnorePattern lets
            // the helm prefix `_` to opt a parameter out explicitly.
            'no-unused-vars': ['warn', {
                argsIgnorePattern: '^_',
                varsIgnorePattern: '^_',
                // Destructuring-into-discard is the common
                // `const [_, num] = match.split(...)` shape; without
                // this the discarded slot lights up the lint and
                // hides the real signal.
                destructuredArrayIgnorePattern: '^_',
                caughtErrorsIgnorePattern: '^_',
            }],
            // Empty `catch {}` blocks: idiom in the codebase is
            // `catch { /* ignore */ }`. allowEmptyCatch:true so the
            // four sites that don't have the comment yet don't fail
            // the gate; the real signal of no-empty (e.g. an empty
            // try block left from a botched refactor) still fires.
            'no-empty': ['error', { allowEmptyCatch: true }],
        },
    },
    {
        // Test files get the same rules but Node-runner globals.
        // Splitting into a separate block is the flat-config idiom
        // for per-file overrides.
        files: ['OnaPlotter/wwwroot/js/**/*.test.js'],
        languageOptions: {
            globals: {
                ...globals.node,
            },
        },
    },
    {
        // Files we don't write but are still inside the lint root.
        // version.g.js is generated by an MSBuild target; its
        // shape is dictated by the writer, not by hand-authored
        // discipline.
        ignores: [
            'OnaPlotter/wwwroot/js/version.g.js',
        ],
    },
];
