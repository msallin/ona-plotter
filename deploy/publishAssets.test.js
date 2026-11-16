// Tests for the Release publish asset steps: the interop bundle
// (scripts/bundle-interop.mjs) and the .br / .gz siblings
// (scripts/precompress.mjs) that deploy/index.js serves.
// Run with: node --test deploy/*.test.js

const { describe, it } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('fs');
const os = require('os');
const path = require('path');
const zlib = require('zlib');
const { pathToFileURL } = require('url');

const scripts = path.join(__dirname, '..', 'scripts');
const tmp = () => fs.mkdtempSync(path.join(os.tmpdir(), 'ona-pub-'));

describe('bundleInterop', () => {
    // A two-module stand-in for leafletInterop.js and one of its imports.
    function sourceTree() {
        const src = tmp();
        fs.writeFileSync(path.join(src, 'helper.js'), 'export const twice = (x) => x * 2;\n');
        fs.writeFileSync(path.join(src, 'leafletInterop.js'),
            "import { twice } from './helper.js';\nexport function initMap(x) { return twice(x); }\n");
        return src;
    }

    it('writes one hashed bundle and a re-export under the stable name', async () => {
        const { bundleInterop } = await import(pathToFileURL(path.join(scripts, 'bundle-interop.mjs')));
        const src = sourceTree();
        const out = tmp();
        const bundle = await bundleInterop(src, out);
        assert.match(bundle, /^leafletInterop\.[A-Z0-9]{8}\.js$/);
        assert.equal(fs.readFileSync(path.join(out, 'leafletInterop.js'), 'utf8'),
            `export * from './${bundle}';\n`);
        assert.ok(!fs.readFileSync(path.join(out, bundle), 'utf8').includes('import'),
            'static imports are inlined');
        const mod = await import(pathToFileURL(path.join(out, 'leafletInterop.js')));
        assert.equal(mod.initMap(21), 42);
    });

    it('replaces the bundle of a previous publish into the same folder', async () => {
        const { bundleInterop } = await import(pathToFileURL(path.join(scripts, 'bundle-interop.mjs')));
        const src = sourceTree();
        const out = tmp();
        fs.writeFileSync(path.join(out, 'leafletInterop.AAAAAAAA.js'), 'stale');
        // The published entry is already a re-export; the source tree wins.
        fs.writeFileSync(path.join(out, 'leafletInterop.js'), "export * from './leafletInterop.AAAAAAAA.js';\n");
        const bundle = await bundleInterop(src, out);
        const bundles = fs.readdirSync(out).filter((f) => /^leafletInterop\.[A-Z0-9]{8}\.js$/.test(f));
        assert.deepEqual(bundles, [bundle]);
    });
});

describe('precompressDir', () => {
    it('writes br and gz siblings that decode to the original', async () => {
        const { precompressDir } = await import(pathToFileURL(path.join(scripts, 'precompress.mjs')));
        const dir = tmp();
        fs.mkdirSync(path.join(dir, 'sub'));
        const css = 'body { color: red; }\n'.repeat(200);
        fs.writeFileSync(path.join(dir, 'sub', 'app.css'), css);
        precompressDir(dir);
        const file = path.join(dir, 'sub', 'app.css');
        assert.equal(zlib.brotliDecompressSync(fs.readFileSync(file + '.br')).toString(), css);
        assert.equal(zlib.gunzipSync(fs.readFileSync(file + '.gz')).toString(), css);
    });

    it('skips images and files that do not shrink', async () => {
        const { precompressDir } = await import(pathToFileURL(path.join(scripts, 'precompress.mjs')));
        const dir = tmp();
        fs.writeFileSync(path.join(dir, 'icon.png'), Buffer.alloc(4096, 7));
        fs.writeFileSync(path.join(dir, 'tiny.js'), 'x');
        precompressDir(dir);
        assert.deepEqual(fs.readdirSync(dir).sort(), ['icon.png', 'tiny.js']);
    });

    it('ignores a missing directory', async () => {
        const { precompressDir } = await import(pathToFileURL(path.join(scripts, 'precompress.mjs')));
        assert.equal(precompressDir(path.join(tmp(), 'nope')), 0);
    });
});
