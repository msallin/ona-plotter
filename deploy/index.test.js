// Tests for the SignalK plugin shim: precompressed static serving and
// the start / stop lifecycle.
// Run with: node --test deploy/*.test.js

const { describe, it } = require('node:test');
const assert = require('node:assert/strict');
const path = require('path');
const fs = require('fs');
const os = require('os');
const {
    acceptableEncodings, contentTypeFor, resolveFrameworkFile,
    createPrecompressedMiddleware, useBeforeStatic, createPlugin,
} = require('./index.js').internals;

describe('acceptableEncodings', () => {
    it('prefers br over gzip for a typical browser header', () => {
        assert.deepEqual(acceptableEncodings('gzip, deflate, br, zstd'), ['br', 'gzip']);
    });

    it('orders by q value', () => {
        assert.deepEqual(acceptableEncodings('gzip;q=1.0, br;q=0.5'), ['gzip', 'br']);
    });

    it('drops encodings refused with q=0', () => {
        assert.deepEqual(acceptableEncodings('br;q=0, gzip'), ['gzip']);
    });

    it('applies the wildcard to unnamed encodings only', () => {
        assert.deepEqual(acceptableEncodings('br;q=0, *'), ['gzip']);
        assert.deepEqual(acceptableEncodings('*;q=0'), []);
    });

    it('accepts nothing for missing, empty or identity-only headers', () => {
        assert.deepEqual(acceptableEncodings(undefined), []);
        assert.deepEqual(acceptableEncodings(''), []);
        assert.deepEqual(acceptableEncodings('identity'), []);
    });

    it('is case-insensitive and tolerates whitespace', () => {
        assert.deepEqual(acceptableEncodings(' GZIP ; q=0.8 ,BR'), ['br', 'gzip']);
    });

    it('ignores a malformed q value instead of refusing the encoding', () => {
        assert.deepEqual(acceptableEncodings('br;q=abc'), ['br']);
    });
});

describe('contentTypeFor', () => {
    it('maps the Blazor asset types', () => {
        assert.equal(contentTypeFor('/x/dotnet.native.wasm'), 'application/wasm');
        assert.equal(contentTypeFor('/x/dotnet.js'), 'text/javascript; charset=utf-8');
        assert.equal(contentTypeFor('/x/blazor.boot.json'), 'application/json; charset=utf-8');
        assert.equal(contentTypeFor('/x/icudt.dat'), 'application/octet-stream');
        assert.equal(contentTypeFor('/x/NoExtension'), 'application/octet-stream');
        assert.equal(contentTypeFor('/x/UPPER.WASM'), 'application/wasm');
    });
});

describe('resolveFrameworkFile', () => {
    const root = path.resolve('/srv/app/public/_framework');

    it('resolves a plain file name', () => {
        assert.equal(resolveFrameworkFile(root, '/dotnet.native.8f1c.wasm'),
            path.join(root, 'dotnet.native.8f1c.wasm'));
    });

    it('decodes percent-escapes', () => {
        assert.equal(resolveFrameworkFile(root, '/a%20b.js'), path.join(root, 'a b.js'));
    });

    it('clamps dot-dot segments to the framework root', () => {
        assert.equal(resolveFrameworkFile(root, '/../../index.html'), path.join(root, 'index.html'));
    });

    it('rejects empty, directory, NUL and malformed paths', () => {
        assert.equal(resolveFrameworkFile(root, ''), null);
        assert.equal(resolveFrameworkFile(root, '/'), null);
        assert.equal(resolveFrameworkFile(root, '/sub/'), null);
        assert.equal(resolveFrameworkFile(root, '/a%00.wasm'), null);
        assert.equal(resolveFrameworkFile(root, '/%E0%A4%A'), null);
    });
});

/** Minimal Express-like response double. */
function fakeRes() {
    const headers = {};
    return {
        headers,
        sent: null,
        headersSent: false,
        append(k, v) { headers[k.toLowerCase()] = headers[k.toLowerCase()] ? headers[k.toLowerCase()] + ', ' + v : v; },
        setHeader(k, v) { headers[k.toLowerCase()] = v; },
        removeHeader(k) { delete headers[k.toLowerCase()]; },
        sendFile(file, cb) { this.sent = file; this.cb = cb; },
    };
}

describe('createPrecompressedMiddleware', () => {
    const root = path.resolve('/srv/app/public/_framework');
    const wasm = path.join(root, 'dotnet.native.wasm');
    const files = new Set([wasm, wasm + '.br', wasm + '.gz', path.join(root, 'only.js')]);
    const exists = (f) => files.has(f);
    const mw = createPrecompressedMiddleware(root, exists);

    function run(req) {
        const res = fakeRes();
        let nextCalls = 0;
        mw({ method: 'GET', headers: {}, ...req }, res, () => nextCalls++);
        return { res, nextCalls };
    }

    it('serves the br sibling with the original content type', () => {
        const { res, nextCalls } = run({ path: '/dotnet.native.wasm', headers: { 'accept-encoding': 'gzip, br' } });
        assert.equal(res.sent, wasm + '.br');
        assert.equal(res.headers['content-encoding'], 'br');
        assert.equal(res.headers['content-type'], 'application/wasm');
        assert.equal(res.headers.vary, 'Accept-Encoding');
        assert.equal(nextCalls, 0);
    });

    it('serves gzip when br is not accepted', () => {
        const { res } = run({ path: '/dotnet.native.wasm', headers: { 'accept-encoding': 'gzip' } });
        assert.equal(res.sent, wasm + '.gz');
        assert.equal(res.headers['content-encoding'], 'gzip');
    });

    it('falls through with Vary when the client accepts no compression', () => {
        const { res, nextCalls } = run({ path: '/dotnet.native.wasm' });
        assert.equal(res.sent, null);
        assert.equal(res.headers.vary, 'Accept-Encoding');
        assert.equal(res.headers['content-encoding'], undefined);
        assert.equal(nextCalls, 1);
    });

    it('falls through when no compressed sibling exists', () => {
        const { res, nextCalls } = run({ path: '/only.js', headers: { 'accept-encoding': 'br, gzip' } });
        assert.equal(res.sent, null);
        assert.equal(nextCalls, 1);
    });

    it('falls through for a missing original even if a sibling exists', () => {
        files.add(path.join(root, 'orphan.js.br'));
        const { res, nextCalls } = run({ path: '/orphan.js', headers: { 'accept-encoding': 'br' } });
        assert.equal(res.sent, null);
        assert.equal(nextCalls, 1);
    });

    it('ignores non-GET/HEAD methods', () => {
        const { res, nextCalls } = run({ method: 'POST', path: '/dotnet.native.wasm', headers: { 'accept-encoding': 'br' } });
        assert.equal(res.sent, null);
        assert.equal(nextCalls, 1);
    });

    it('serves HEAD like GET', () => {
        const { res } = run({ method: 'HEAD', path: '/dotnet.native.wasm', headers: { 'accept-encoding': 'br' } });
        assert.equal(res.sent, wasm + '.br');
    });

    it('undoes its headers and falls through when sending fails', () => {
        // The sibling vanished between the exists check and the send.
        const res = fakeRes();
        let nextCalls = 0;
        mw({ method: 'GET', path: '/dotnet.native.wasm', headers: { 'accept-encoding': 'br' } },
            res, () => nextCalls++);
        assert.equal(nextCalls, 0);
        res.cb(new Error('ENOENT'));
        assert.equal(res.headers['content-encoding'], undefined);
        assert.equal(res.headers['content-type'], undefined);
        assert.equal(nextCalls, 1);
    });
});

describe('useBeforeStatic', () => {
    function fakeApp(layers, key = '_router') {
        const stack = layers.map((l) => ({ ...l }));
        const app = {
            [key]: { stack },
            use(mount, fn) { stack.push({ name: fn.name, handle: fn, match: (p) => p.startsWith(mount) }); },
        };
        return { app, stack };
    }
    const staticLayer = { name: 'serveStatic', handle() { }, match: (p) => p.startsWith('/pkg') };
    const authLayer = { name: 'auth', handle() { }, match: () => true };

    it('moves the middleware directly in front of the matching static layer', () => {
        const { app, stack } = fakeApp([authLayer, staticLayer, { name: 'other', match: () => true }]);
        function mw() { }
        assert.equal(useBeforeStatic(app, '/pkg/_framework', mw, '/pkg/_framework/probe.wasm'), true);
        assert.deepEqual(stack.map((l) => l.name), ['auth', 'mw', 'serveStatic', 'other']);
    });

    it('supports the Express 5 router property', () => {
        const { app, stack } = fakeApp([staticLayer], 'router');
        function mw() { }
        assert.equal(useBeforeStatic(app, '/pkg/_framework', mw, '/pkg/_framework/probe.wasm'), true);
        assert.equal(stack[0].name, 'mw');
    });

    it('leaves the layer in place when no static layer matches', () => {
        const { app, stack } = fakeApp([{ name: 'serveStatic', handle() { }, match: (p) => p.startsWith('/other') }]);
        function mw() { }
        assert.equal(useBeforeStatic(app, '/pkg/_framework', mw, '/pkg/_framework/probe.wasm'), false);
        assert.equal(stack[stack.length - 1].name, 'mw');
    });

    it('returns false when the router internals are unknown', () => {
        const app = { use() { } };
        assert.equal(useBeforeStatic(app, '/pkg/_framework', function mw() { }, '/pkg/_framework/x'), false);
    });
});

describe('plugin lifecycle', () => {
    // Deployed layout: <tmp>/signalk-onaplotter/public/index.html
    function deployDir() {
        const base = path.join(fs.mkdtempSync(path.join(os.tmpdir(), 'ona-')), 'signalk-onaplotter');
        fs.mkdirSync(path.join(base, 'public'), { recursive: true });
        fs.writeFileSync(path.join(base, 'public', 'index.html'), '<html></html>');
        return base;
    }

    // Express-like app double recording every registration.
    function fakeApp() {
        const routes = { get: [], post: [], use: [] };
        return {
            routes,
            _router: { stack: [] },
            get(p, h) { routes.get.push({ p, h }); },
            post(p, h) { routes.post.push({ p, h }); },
            use(p, h) { routes.use.push({ p, h }); this._router.stack.push({ name: h.name, handle: h }); },
            setPluginStatus() {},
            setPluginError() {},
            debug() {},
            error() {},
        };
    }

    function call(handler) {
        let nextCalls = 0;
        const res = { sent: null, sendFile(f) { this.sent = f; }, json(b) { this.sent = b; } };
        handler({ method: 'GET', path: '/x.js', headers: {}, body: { message: 'm' } }, res, () => nextCalls++);
        return { res, nextCalls };
    }

    it('registers each route once across repeated starts', () => {
        const app = fakeApp();
        const plugin = createPlugin(app, deployDir());
        plugin.start();
        plugin.stop();
        plugin.start();
        plugin.start();
        assert.equal(app.routes.post.length, 1);
        assert.equal(app.routes.get.length, 1);
        assert.equal(app.routes.use.length, 4, '_framework, js, css, lib');
    });

    it('serves the SPA fallback while running', () => {
        const app = fakeApp();
        const base = deployDir();
        createPlugin(app, base).start();
        const { res, nextCalls } = call(app.routes.get[0].h);
        assert.equal(res.sent, path.join(base, 'public', 'index.html'));
        assert.equal(nextCalls, 0);
    });

    it('falls through on every route once stopped', () => {
        const app = fakeApp();
        const plugin = createPlugin(app, deployDir());
        plugin.start();
        plugin.stop();
        for (const h of [app.routes.get[0].h, app.routes.post[0].h, app.routes.use[0].h]) {
            const { res, nextCalls } = call(h);
            assert.equal(nextCalls, 1);
            assert.equal(res.sent, null);
        }
    });

    it('a restart re-enables the existing routes', () => {
        const app = fakeApp();
        const plugin = createPlugin(app, deployDir());
        plugin.start();
        plugin.stop();
        plugin.start();
        assert.equal(call(app.routes.get[0].h).nextCalls, 0);
    });

    it('stays stopped when the bundle is missing', () => {
        const app = fakeApp();
        let error = null;
        app.setPluginError = (e) => { error = e; };
        const base = path.join(fs.mkdtempSync(path.join(os.tmpdir(), 'ona-')), 'signalk-onaplotter');
        createPlugin(app, base).start();
        assert.match(error, /index\.html missing/);
        assert.equal(app.routes.get.length, 0);
    });

    it('mounts the precompressed dirs under the package prefix', () => {
        const app = fakeApp();
        createPlugin(app, deployDir()).start();
        assert.deepEqual(app.routes.use.map((r) => r.p), [
            '/signalk-onaplotter/_framework', '/signalk-onaplotter/js',
            '/signalk-onaplotter/css', '/signalk-onaplotter/lib',
        ]);
    });
});
