// Repo-root ESLint config. Lints the hand-written JS interop layer
// under OnaPlotter/wwwroot/js so runtime-only crashes (a TDZ
// ReferenceError on a `const` read before its declaration, an
// undefined global) are caught at review time, not on a freshly
// loaded helm tablet.
//
// Scope: OnaPlotter/wwwroot/js, the deploy/ plugin shim and the
// scripts/ build helpers. The Razor-managed WASM runtime,
// OnaPlotter.UiTests (Playwright) and node_modules are outside this
// config; they have their own toolchains.
//
// Rule philosophy: tight runtime-correctness rules, no stylistic
// rules. The codebase is hand-formatted and consistent; `semi`,
// `quotes`, `indent` etc. would generate a large cosmetic diff that
// buries the actual signal. Likely bugs (use-before-define, no-undef,
// dupe-keys) are errors; taste calls (arrow style, comma dangle) stay
// off.

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
                // calling JS interop.
                L: 'readonly',
            },
        },
        rules: {
            // Catches the TDZ bug class: a `const X` used at
            // module-init time before its declaration line. Tuning:
            //   functions:false - function declarations hoist, so
            //     forward refs to `function foo()` are legitimate at
            //     runtime. Only `let`/`const`/`class` are in TDZ.
            //   classes:false - L.Layer.extend({...}) and similar
            //     class-style declarations are referenced inside
            //     deferred callbacks (Leaflet draws, mode handlers)
            //     that fire only after module init completes, so TDZ
            //     can't trip in the patterns this codebase uses;
            //     `true` reports false positives in radarLayer.js.
            //   variables:true - the actual signal.
            'no-use-before-define': ['error', {
                functions: false,
                classes: false,
                variables: true,
            }],
            // ESLint recommended already enables most of these;
            // explicit listing makes the intent visible at config-
            // review time rather than buried in a preset.
            'no-undef': 'error',
            'no-redeclare': 'error',
            'no-const-assign': 'error',
            'no-dupe-keys': 'error',
            'no-dupe-args': 'error',
            'no-unreachable': 'error',
            // Unused vars are warnings, not errors: the JS interop
            // code takes options bags from C# whose shape may carry
            // fields a given renderer ignores. A `_` prefix opts a
            // parameter or binding out explicitly.
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
            // Empty `catch {}` blocks are an accepted idiom here
            // (`catch { /* ignore */ }`); the real signal of no-empty
            // (an empty try or if block) still fires.
            'no-empty': ['error', { allowEmptyCatch: true }],
        },
    },
    {
        // Test files get the same rules but Node-runner globals.
        files: ['OnaPlotter/wwwroot/js/**/*.test.js'],
        languageOptions: {
            globals: {
                ...globals.node,
            },
        },
    },
    {
        // The SignalK plugin shim runs in Node as CommonJS (loaded by
        // signalk-server), not in the browser.
        files: ['deploy/**/*.js'],
        languageOptions: {
            ecmaVersion: 2022,
            sourceType: 'commonjs',
            globals: {
                ...globals.node,
            },
        },
    },
    {
        // Build helpers invoked by MSBuild targets in
        // OnaPlotter.csproj (interop bundle, CACHE_NAME stamp,
        // precompression) plus their node:test suites. Node ESM.
        files: ['scripts/**/*.mjs'],
        languageOptions: {
            ecmaVersion: 2022,
            sourceType: 'module',
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
