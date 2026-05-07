// Render the OnaPlotter favicon SVG into the PNG sizes iOS / iPadOS
// requires for the home-screen / PWA install path. iPad rejects SVG
// for `apple-touch-icon` so without these PNGs the home-screen tile
// silently falls back to a screenshot of the page (the user reported
// this - "OpenPlotter icon isn't shown in the PWA installation").
//
// Uses Playwright (already a dev dep for UI tests) so no extra
// install is needed - just `node scripts/generate-pwa-icons.mjs`.
// The browser renders the SVG at the requested viewport size and
// screenshots a transparent-bg PNG.
//
// Re-run after editing favicon.svg. Bump service-worker.js
// CACHE_NAME so the new icons evict the cached PNG copies in
// existing PWA installs.

import { chromium } from 'playwright';
import { readFileSync, writeFileSync, existsSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '..');
const svgPath = resolve(root, 'OnaPlotter/wwwroot/favicon.svg');
const outDir = resolve(root, 'OnaPlotter/wwwroot');

if (!existsSync(svgPath)) {
    console.error(`ERROR: ${svgPath} not found`);
    process.exit(1);
}

const svgRaw = readFileSync(svgPath, 'utf8');

// Wrap the SVG in a minimal HTML doc so Playwright renders it at
// the requested CSS pixel size with a transparent background and
// no document margins. Inline-style `width`/`height` on the <svg>
// override its viewBox-based sizing so the rasteriser produces
// exactly the pixel grid we asked for.
function makeDoc(size) {
    // Force the SVG to fill the viewport. Strip the SVG's existing
    // width/height attributes so our forced ones win.
    const svg = svgRaw
        .replace(/\s(width|height)="[^"]*"/g, '')
        .replace(/<svg\s/, `<svg width="${size}" height="${size}" `);
    return `<!doctype html>
<html><head><style>
html, body { margin: 0; padding: 0; background: transparent; }
svg { display: block; }
</style></head><body>${svg}</body></html>`;
}

const targets = [
    { size: 180, name: 'apple-touch-icon-180.png' },
    { size: 192, name: 'icon-192.png' },
    { size: 512, name: 'icon-512.png' },
];

const browser = await chromium.launch();
try {
    for (const { size, name } of targets) {
        const ctx = await browser.newContext({
            viewport: { width: size, height: size },
            deviceScaleFactor: 1,
        });
        const page = await ctx.newPage();
        await page.setContent(makeDoc(size), { waitUntil: 'load' });
        const buf = await page.screenshot({
            type: 'png',
            omitBackground: true,
            clip: { x: 0, y: 0, width: size, height: size },
        });
        const outPath = resolve(outDir, name);
        writeFileSync(outPath, buf);
        console.log(`  -> ${outPath} (${size}x${size}, ${buf.length} bytes)`);
        await ctx.close();
    }
} finally {
    await browser.close();
}
console.log('Done. Bump service-worker.js CACHE_NAME if the icons changed.');
