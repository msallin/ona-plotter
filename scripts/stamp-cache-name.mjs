#!/usr/bin/env node
//
// Stamps the service worker's CACHE_NAME with a deploy-unique identifier
// so a new build invalidates the browser's installed cache. Without this,
// the SW reuses the cache keyed by the hand-bumped 'ona-plotter-vNNN'
// literal, and stale .wasm / .js / .css get served from cache until the
// helm hard-refreshes.
//
// Invoked from OnaPlotter.csproj's StampPublishedServiceWorker target on
// Release publish. Two args:
//
//   node stamp-cache-name.mjs <path-to-service-worker.js> <git-hash>
//
// Patches in place. Idempotent: matches /ona-plotter-[\w-]+/ at the
// CACHE_NAME assignment line so re-running with a different hash works.
// Fails non-zero (and explains) if no match is found - a future rename
// of CACHE_NAME would otherwise silently skip the stamp and let stale
// caches leak through.
//

import { readFileSync, writeFileSync } from 'node:fs';

const [, , swPath, hash] = process.argv;
if (!swPath || !hash) {
    console.error('usage: stamp-cache-name.mjs <path> <hash>');
    process.exit(2);
}

const src = readFileSync(swPath, 'utf8');

// Anchor the regex on the CACHE_NAME assignment to avoid catching the
// TILE_CACHE_NAME (which has its own lifecycle) or any incidental
// "ona-plotter-..." substring elsewhere in the file.
const pattern = /(const CACHE_NAME = ')ona-plotter-[\w-]+(')/;
if (!pattern.test(src)) {
    console.error(
        `stamp-cache-name: no 'const CACHE_NAME = "ona-plotter-..."' line ` +
        `found in ${swPath}. Did the assignment shape change?`,
    );
    process.exit(1);
}

const patched = src.replace(pattern, `$1ona-plotter-${hash}$2`);
writeFileSync(swPath, patched);
console.log(`stamp-cache-name: CACHE_NAME = ona-plotter-${hash} in ${swPath}`);
