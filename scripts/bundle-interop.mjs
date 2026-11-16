#!/usr/bin/env node
//
// Bundles leafletInterop.js and every module it statically imports into
// one minified, content-hashed ES module for the Release publish, and
// replaces the published leafletInterop.js with a one-line re-export of
// it. A cold boot then loads two scripts instead of ~30, each of which
// would otherwise go through the service worker's network-first race.
//
//   node bundle-interop.mjs <source-js-dir> <published-js-dir>
//
// Example output in <published-js-dir>:
//   leafletInterop.5QWJ3KXR.js   the bundle (immutable: the hash names it)
//   leafletInterop.js            export * from './leafletInterop.5QWJ3KXR.js';
//
// C# keeps importing "./js/leafletInterop.js" in Debug (unbundled
// sources) and Release (the re-export), so both get one module instance.
// The bundle is built from the SOURCE tree: the published copy may
// already be the re-export from a previous publish into the same folder.
// Modules C# imports on their own (historyMapLayer.js, platform/*,
// visibility.js, deadman.js, searchBoxJs.js) share nothing with this
// graph and stay separate files.

import { build } from 'esbuild';
import { readdirSync, rmSync, writeFileSync } from 'node:fs';
import { basename, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const ENTRY = 'leafletInterop.js';

// Earlier bundles in the publish folder: "leafletInterop.<HASH>.js".
const HASHED_BUNDLE = /^leafletInterop\.[A-Z0-9]{8}\.js$/;

/**
 * Builds the bundle into outJsDir and rewrites outJsDir/leafletInterop.js
 * as its re-export. Returns the bundle's file name.
 */
export async function bundleInterop(srcJsDir, outJsDir) {
    for (const name of readdirSync(outJsDir)) {
        if (HASHED_BUNDLE.test(name)) rmSync(join(outJsDir, name));
    }
    const result = await build({
        entryPoints: [join(srcJsDir, ENTRY)],
        bundle: true,
        format: 'esm',
        minify: true,
        entryNames: '[name].[hash]',
        outdir: outJsDir,
        metafile: true,
        legalComments: 'none',
        logLevel: 'warning',
    });
    const bundle = Object.keys(result.metafile.outputs)
        .map((f) => basename(f))
        .find((f) => HASHED_BUNDLE.test(f));
    if (!bundle) throw new Error('bundle-interop: esbuild produced no hashed leafletInterop bundle');
    writeFileSync(join(outJsDir, ENTRY), `export * from './${bundle}';\n`);
    return bundle;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    const [, , srcJsDir, outJsDir] = process.argv;
    if (!srcJsDir || !outJsDir) {
        console.error('usage: bundle-interop.mjs <source-js-dir> <published-js-dir>');
        process.exit(2);
    }
    const bundle = await bundleInterop(srcJsDir, outJsDir);
    console.log(`bundle-interop: ${ENTRY} -> ${bundle}`);
}
