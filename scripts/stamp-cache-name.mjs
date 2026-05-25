#!/usr/bin/env node
//
// Stamps the service worker's CACHE_NAME with a CONTENT-based
// identifier derived from the APP_SHELL files + the service worker
// source itself. A deploy that only touches C# code (no CSS, no
// icons, no JS module additions, no Leaflet upgrade, no SW logic
// change) produces the IDENTICAL stamp and therefore does NOT
// invalidate the helm's cached app-shell. Without that,
// b126a86's commit-hash stamp bumped the cache on every Release
// publish - and on the next page load the SW's network-first
// handler had to re-fetch every /js/* and /css/* module from the
// upstream, which on a Pi-class SK Node host can stall long enough
// to freeze the WASM bootstrap.
//
// Invoked from OnaPlotter.csproj's StampPublishedServiceWorker
// target on Release publish:
//
//   node stamp-cache-name.mjs <path-to-service-worker.js> [<fallback-hash>]
//
// The fallback-hash arg is honoured only if the content-hash
// computation fails (e.g. APP_SHELL parse mismatch, missing shell
// files). Keeps the build deterministic even in environments where
// the wwwroot tree is unusual.
//
// Patches in place. Idempotent: matches /ona-plotter-[\w-]+/ at the
// CACHE_NAME assignment line so re-running with the same content
// re-stamps the same value (no-op write). Fails non-zero if no
// CACHE_NAME line is found - a future rename would otherwise
// silently skip the stamp and let stale caches leak through.
//

import { readFileSync, writeFileSync, existsSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { createHash } from 'node:crypto';

const [, , swPath, fallbackHash] = process.argv;
if (!swPath) {
    console.error('usage: stamp-cache-name.mjs <path-to-service-worker.js> [<fallback-hash>]');
    process.exit(2);
}

const src = readFileSync(swPath, 'utf8');

// Anchor the regex on the CACHE_NAME assignment to avoid catching
// the TILE_CACHE_NAME (which has its own lifecycle) or any incidental
// "ona-plotter-..." substring elsewhere in the file.
const cachePattern = /(const CACHE_NAME = ')ona-plotter-[\w-]+(')/;
if (!cachePattern.test(src)) {
    console.error(
        `stamp-cache-name: no 'const CACHE_NAME = "ona-plotter-..."' line ` +
        `found in ${swPath}. Did the assignment shape change?`,
    );
    process.exit(1);
}

const stamp = computeShellContentHash(src, swPath) ?? fallbackHash;
if (!stamp) {
    console.error(
        'stamp-cache-name: content-hash computation failed and no fallback hash was provided.',
    );
    process.exit(1);
}

const patched = src.replace(cachePattern, `$1ona-plotter-${stamp}$2`);
writeFileSync(swPath, patched);
console.log(`stamp-cache-name: CACHE_NAME = ona-plotter-${stamp} in ${swPath}`);


/**
 * Compute a 12-hex-char fingerprint over the SW source + every
 * APP_SHELL file's contents. Returns null on any failure so the
 * caller can fall back to the optional hash arg.
 *
 * The hash deliberately includes:
 *   * The SW source itself (so a change to the SW logic - new
 *     fetch rule, changed cache strategy - bumps the cache even
 *     if no shell file changed).
 *   * Each APP_SHELL file's RELATIVE PATH followed by its
 *     contents (so renaming a shell entry without changing the
 *     bytes still bumps; and the boundary between adjacent files
 *     can't be ambiguous in the resulting digest).
 *   * The literal string 'MISSING:<rel>' for any APP_SHELL entry
 *     that isn't on disk (so a future re-add re-stabilises the
 *     hash without poisoning the cache silently).
 *
 * The hash deliberately EXCLUDES:
 *   * The build timestamp / commit hash - those are the values
 *     that made the previous stamp churn unnecessarily.
 *   * The CACHE_NAME value itself in the SW source. We replace
 *     it with a fixed sentinel before hashing so re-stamping an
 *     already-stamped file is idempotent (otherwise the SW
 *     source bytes differ between deploys solely because of the
 *     previous stamp's output, and the hash would never stabilise).
 */
function computeShellContentHash(swSource, swFilePath) {
    try {
        const paths = extractAppShellPaths(swSource);
        const wwwroot = dirname(swFilePath);
        const h = createHash('sha256');

        // Iterate in sorted order so the digest is stable across
        // OS / filesystem listing differences.
        for (const rel of [...paths].sort()) {
            const full = resolve(wwwroot, rel);
            h.update(`PATH:${rel}\n`);
            if (!existsSync(full)) {
                h.update('MISSING\n');
                continue;
            }
            h.update(readFileSync(full));
            h.update('\n');
        }
        // SW source: a behaviour change here should always bump
        // the cache even if all shell file bytes are identical.
        // Normalise the CACHE_NAME literal to a fixed sentinel
        // before hashing so the input is the same whether we're
        // hashing pristine source or a previously-stamped output.
        const normalised = swSource.replace(
            cachePattern, '$1ona-plotter-NORMALISED$2');
        h.update('PATH:service-worker.js\n');
        h.update(normalised);
        return h.digest('hex').slice(0, 12);
    } catch (err) {
        console.warn(`stamp-cache-name: content hashing failed (${err.message}); ` +
                     'will fall back to passed hash arg if present');
        return null;
    }
}

/**
 * Pulls the relative paths declared in APP_SHELL out of the SW
 * source. Two shapes to handle:
 *
 *   1. `new URL('relative/path', SCOPE).toString()` - the dominant
 *      shape; matches every css/js/png entry in the list.
 *   2. The bare `SCOPE` first entry - represents the page document
 *      (index.html). We add 'index.html' explicitly.
 *
 * Returns a Set so duplicates collapse. Throwing on no matches
 * would force a fallback-only stamp, which is the SAFER default
 * than silently hashing an empty shell.
 */
function extractAppShellPaths(swSource) {
    const paths = new Set();
    const re = /new URL\(\s*['"]([^'"]+)['"]\s*,\s*SCOPE\s*\)/g;
    let m;
    while ((m = re.exec(swSource)) !== null) {
        paths.add(m[1]);
    }
    if (paths.size === 0) {
        throw new Error('no APP_SHELL `new URL(\'...\', SCOPE)` entries found');
    }
    // SCOPE itself is the first APP_SHELL entry and represents
    // the page document. Mirror that explicitly so a change to
    // index.html bumps the cache even though it isn't a `new URL`
    // entry.
    paths.add('index.html');
    return paths;
}
