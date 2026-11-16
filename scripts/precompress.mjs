#!/usr/bin/env node
//
// Writes brotli (.br) and gzip (.gz) siblings for the text assets under
// the given directories. Runs on the Release publish after the scripts
// and stylesheets were minified, bundled and stamped, so the siblings
// match the bytes on disk; the SignalK plugin shim (deploy/index.js)
// serves them to clients that accept the encoding.
//
//   node precompress.mjs <dir> [<dir> ...]
//
// Example: js/leafletInterop.5QWJ3KXR.js (180 KB)
//       -> js/leafletInterop.5QWJ3KXR.js.br (~40 KB) and .js.gz (~50 KB)
// A sibling that would not be smaller than the original is not written
// (or removed if left over), so the server falls back to identity.

import { readdirSync, readFileSync, rmSync, statSync, writeFileSync, existsSync } from 'node:fs';
import { extname, join, resolve } from 'node:path';
import { brotliCompressSync, gzipSync, constants } from 'node:zlib';
import { fileURLToPath } from 'node:url';

/** Extensions worth compressing; images and fonts are already compressed. */
export const COMPRESSIBLE = new Set(['.js', '.mjs', '.css', '.json', '.svg', '.html', '.map']);

const ENCODERS = [
    ['.br', (buf) => brotliCompressSync(buf, {
        params: { [constants.BROTLI_PARAM_QUALITY]: 11, [constants.BROTLI_PARAM_SIZE_HINT]: buf.length },
    })],
    ['.gz', (buf) => gzipSync(buf, { level: 9 })],
];

function* walk(dir) {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
        const full = join(dir, entry.name);
        if (entry.isDirectory()) yield* walk(full);
        else if (entry.isFile()) yield full;
    }
}

/** Compresses every compressible file under dir; returns how many were written. */
export function precompressDir(dir) {
    if (!existsSync(dir) || !statSync(dir).isDirectory()) return 0;
    let written = 0;
    for (const file of walk(dir)) {
        if (!COMPRESSIBLE.has(extname(file).toLowerCase())) continue;
        const original = readFileSync(file);
        for (const [suffix, encode] of ENCODERS) {
            const compressed = encode(original);
            if (compressed.length < original.length) {
                writeFileSync(file + suffix, compressed);
                written++;
            } else {
                rmSync(file + suffix, { force: true });
            }
        }
    }
    return written;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    const dirs = process.argv.slice(2);
    if (dirs.length === 0) {
        console.error('usage: precompress.mjs <dir> [<dir> ...]');
        process.exit(2);
    }
    let total = 0;
    for (const dir of dirs) total += precompressDir(dir);
    console.log(`precompress: wrote ${total} .br/.gz siblings`);
}
