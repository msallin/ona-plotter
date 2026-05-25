// Tests for stamp-cache-name.mjs. Run via:
//   node --test scripts/__tests__/stamp-cache-name.test.mjs
//
// .mjs so the file-extension picks ESM without a package.json. The
// script runs as a child process so we exercise the CLI surface the
// MSBuild target actually invokes; that's the integration boundary
// we care about pinning. Reaching into the script's internals would
// miss bugs like "the argv parsing reordered" or "the regex anchor
// drifted across edits".

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const SCRIPT = fileURLToPath(new URL('../stamp-cache-name.mjs', import.meta.url));

// --- helpers --------------------------------------------------------

/**
 * Build a temp dir laid out like a published wwwroot:
 *
 *   <tmp>/
 *     service-worker.js     (source matching the real shape)
 *     index.html
 *     css/app.css
 *     js/errorRelayBoot.js
 *
 * The SW source mirrors the APP_SHELL fragment from the real file -
 * the script's regex parses the same `new URL('...', SCOPE)` literal
 * pattern. Returns the tmp dir so the caller can mutate files +
 * cleanup.
 */
function setupTempWwwroot(shellFiles = {
    'index.html': '<html><body>seed</body></html>',
    'css/app.css': '/* seed */',
    'js/errorRelayBoot.js': 'console.log("seed");',
}) {
    const dir = mkdtempSync(join(tmpdir(), 'stamp-cache-test-'));
    for (const [rel, content] of Object.entries(shellFiles)) {
        const full = join(dir, rel);
        mkdirSync(join(full, '..'), { recursive: true });
        writeFileSync(full, content);
    }
    writeFileSync(join(dir, 'service-worker.js'), buildSwSource());
    return dir;
}

/** Minimal SW source: a CACHE_NAME line + an APP_SHELL fragment in
 *  the exact shape the parser expects. Anything else the real SW
 *  has (fetch handler etc.) is irrelevant to the stamp. */
function buildSwSource() {
    return [
        `const CACHE_NAME = 'ona-plotter-vSEED';`,
        `const TILE_CACHE_NAME = 'ona-plotter-tiles-v1';`,
        `const SCOPE = self.registration ? self.registration.scope : self.location.href;`,
        `const APP_SHELL = [`,
        `    SCOPE,`,
        `    new URL('css/app.css', SCOPE).toString(),`,
        `    new URL('js/errorRelayBoot.js', SCOPE).toString(),`,
        `];`,
        ``,
    ].join('\n');
}

function readCacheName(swPath) {
    const src = readFileSync(swPath, 'utf8');
    const m = src.match(/const CACHE_NAME = '(ona-plotter-[\w-]+)'/);
    if (!m) throw new Error('CACHE_NAME not found after stamp');
    return m[1];
}

/** Run the script and return stdout. Throws on non-zero exit. */
function runStamp(swPath, fallbackHash) {
    const args = fallbackHash !== undefined
        ? [SCRIPT, swPath, fallbackHash]
        : [SCRIPT, swPath];
    return execFileSync('node', args, { encoding: 'utf8' });
}

// --- tests ----------------------------------------------------------

test('content-based stamp: same shell contents -> same CACHE_NAME', () => {
    // The whole point of the content-based stamp: a no-shell-change
    // deploy (a C#-only PR, a doc tweak) doesn't bump the cache, so
    // the helm's app-shell cache survives across deploys and the
    // network-first /js/* handler doesn't have to re-fetch the world
    // on the next page load.
    const dir = setupTempWwwroot();
    try {
        runStamp(join(dir, 'service-worker.js'));
        const first = readCacheName(join(dir, 'service-worker.js'));

        // Restore SW source to a fresh seed (the stamp wrote to it)
        // then re-stamp. Shell files are byte-identical -> hash
        // should be identical too.
        writeFileSync(join(dir, 'service-worker.js'), buildSwSource());
        runStamp(join(dir, 'service-worker.js'));
        const second = readCacheName(join(dir, 'service-worker.js'));

        assert.equal(first, second, 'stamp should be stable across identical inputs');
        assert.match(first, /^ona-plotter-[a-f0-9]{12}$/);
    } finally {
        rmSync(dir, { recursive: true, force: true });
    }
});

test('content-based stamp: changing a shell file bumps the CACHE_NAME', () => {
    // The other half of the contract: when something DOES change in
    // the shell, the helm must pick it up. Without this, the helm
    // would keep serving the previous CSS / boot script forever
    // (until a manual cache clear), which is the bug the original
    // git-hash stamp existed to fix.
    const dir = setupTempWwwroot();
    try {
        runStamp(join(dir, 'service-worker.js'));
        const before = readCacheName(join(dir, 'service-worker.js'));

        // Mutate one shell file - reset SW source so we're stamping
        // a fresh seed (no stale stamp from the previous run).
        writeFileSync(join(dir, 'css/app.css'), '/* NEW VERSION */');
        writeFileSync(join(dir, 'service-worker.js'), buildSwSource());
        runStamp(join(dir, 'service-worker.js'));
        const after = readCacheName(join(dir, 'service-worker.js'));

        assert.notEqual(before, after, 'shell change should bump the stamp');
    } finally {
        rmSync(dir, { recursive: true, force: true });
    }
});

test('content-based stamp: SW source change bumps the CACHE_NAME', () => {
    // A SW logic change (new fetch rule, new cache strategy) is a
    // helm-visible behaviour change that warrants a cache bump even
    // when no shell file changed. Hashing the SW source itself into
    // the digest captures that.
    const dir = setupTempWwwroot();
    try {
        runStamp(join(dir, 'service-worker.js'));
        const before = readCacheName(join(dir, 'service-worker.js'));

        // Add a comment to the SW source (no shell change) then
        // re-stamp from the modified-but-not-yet-stamped baseline.
        const swPath = join(dir, 'service-worker.js');
        writeFileSync(swPath, buildSwSource() + '// behaviour tweak\n');
        runStamp(swPath);
        const after = readCacheName(swPath);

        assert.notEqual(before, after, 'SW source change should bump the stamp');
    } finally {
        rmSync(dir, { recursive: true, force: true });
    }
});

test('fallback hash is used when no APP_SHELL entries are found', () => {
    // Defensive: if the SW file ever loses its `new URL(..., SCOPE)`
    // pattern (e.g. someone refactors the shell list into a fetch
    // call), the script should fall back to the passed hash rather
    // than silently producing an empty-shell digest. Surfaced via
    // stderr so the build still logs the regression.
    const dir = mkdtempSync(join(tmpdir(), 'stamp-cache-fallback-'));
    try {
        const swPath = join(dir, 'service-worker.js');
        writeFileSync(swPath, `const CACHE_NAME = 'ona-plotter-vSEED';\n`);
        runStamp(swPath, 'deadbeef');
        assert.equal(readCacheName(swPath), 'ona-plotter-deadbeef');
    } finally {
        rmSync(dir, { recursive: true, force: true });
    }
});

test('missing shell file degrades cleanly without throwing', () => {
    // A shell file declared in APP_SHELL but absent on disk shouldn't
    // crash the build - it logs the MISSING marker into the digest
    // and continues. The stamp still produces a value; a later
    // re-add of the file lands a new (different) value. This is the
    // "fail open" choice we made in the script: a missing file is
    // a real bug, but the SW install will surface it loudly (cache
    // .addAll rejects); the stamp shouldn't ALSO take the build
    // down.
    const dir = setupTempWwwroot();
    try {
        // Delete one shell file - SW source still references it.
        rmSync(join(dir, 'js/errorRelayBoot.js'));
        const out = runStamp(join(dir, 'service-worker.js'));
        assert.match(out, /^stamp-cache-name: CACHE_NAME = ona-plotter-[a-f0-9]{12}/m);
    } finally {
        rmSync(dir, { recursive: true, force: true });
    }
});

test('CACHE_NAME assignment missing -> non-zero exit', () => {
    // The script's hard contract: if it can't find the line to
    // patch, it fails LOUDLY. Silent skip would mean the SW ships
    // with whatever the source CACHE_NAME literal is, which is
    // exactly the stale-cache class of bug the stamp exists to
    // prevent.
    const dir = mkdtempSync(join(tmpdir(), 'stamp-cache-noname-'));
    try {
        const swPath = join(dir, 'service-worker.js');
        writeFileSync(swPath, '// no CACHE_NAME here\n');
        assert.throws(() => runStamp(swPath, 'fallback'),
                      /stamp-cache-name|exited with/);
    } finally {
        rmSync(dir, { recursive: true, force: true });
    }
});

test('TILE_CACHE_NAME is left untouched', () => {
    // Regression guard: TILE_CACHE_NAME shares the 'ona-plotter-...'
    // prefix but has its own (rare) bump policy. The CACHE_NAME
    // regex must be anchored to the const-assignment to avoid
    // catching the tiles one. Pin via observation: after stamp, the
    // TILE_CACHE_NAME line should be unchanged.
    const dir = setupTempWwwroot();
    try {
        const swPath = join(dir, 'service-worker.js');
        runStamp(swPath);
        const src = readFileSync(swPath, 'utf8');
        assert.match(src, /TILE_CACHE_NAME = 'ona-plotter-tiles-v1'/);
    } finally {
        rmSync(dir, { recursive: true, force: true });
    }
});

test('idempotent: re-stamping the already-stamped file produces the same value', () => {
    // Hot dev loop case: someone runs `dotnet publish` twice with no
    // intervening source change. The second run's stamp should match
    // the first (no-op write to the SW file). The previous git-hash
    // stamp already had this property (same hash -> same stamp);
    // the content-hash variant inherits it.
    const dir = setupTempWwwroot();
    try {
        const swPath = join(dir, 'service-worker.js');
        runStamp(swPath);
        const first = readCacheName(swPath);
        runStamp(swPath);
        const second = readCacheName(swPath);
        assert.equal(first, second);
    } finally {
        rmSync(dir, { recursive: true, force: true });
    }
});
