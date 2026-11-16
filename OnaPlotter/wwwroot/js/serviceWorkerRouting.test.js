// Tests for service-worker.js request routing (routeFor).
// The worker is a classic script, so it is evaluated in a vm context with
// just enough of the ServiceWorkerGlobalScope stubbed for top-level code.
// Run with: node --test OnaPlotter/wwwroot/js/serviceWorkerRouting.test.js

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';

function loadWorker(scope) {
    const src = readFileSync(fileURLToPath(new URL('../service-worker.js', import.meta.url)), 'utf8');
    const listeners = {};
    const context = {
        URL,
        self: {
            registration: { scope },
            location: new URL('service-worker.js', scope),
            addEventListener: (type, fn) => { listeners[type] = fn; },
        },
    };
    vm.createContext(context);
    vm.runInContext(src, context);
    return {
        routeFor: context.routeFor, scopePath: new URL(scope).pathname, listeners, context,
    };
}

const sub = loadWorker('http://boat.local:3000/signalk-onaplotter/');
const root = loadWorker('http://localhost:5282/');

describe('routeFor under the SignalK webapp subpath', () => {
    const r = (p) => sub.routeFor(p, sub.scopePath);

    it('keeps js/ and css/ network-first', () => {
        assert.equal(r('/signalk-onaplotter/js/leafletInterop.js'), 'network-first');
        assert.equal(r('/signalk-onaplotter/js/platform/viewport.js'), 'network-first');
        assert.equal(r('/signalk-onaplotter/css/app.css'), 'network-first');
    });

    it('treats the scope root and html documents as network-first', () => {
        assert.equal(r('/signalk-onaplotter/'), 'network-first');
        assert.equal(r('/signalk-onaplotter/index.html'), 'network-first');
    });

    it('serves fingerprinted _framework files as immutable', () => {
        assert.equal(r('/signalk-onaplotter/_framework/dotnet.native.l9w6gmu0b1.wasm'), 'immutable');
        assert.equal(r('/signalk-onaplotter/_framework/System.Private.CoreLib.x7k2p0q9ab.wasm'), 'immutable');
        assert.equal(r('/signalk-onaplotter/_framework/dotnet.0a1b2c3d4e.js'), 'immutable');
    });

    it('serves the hashed Release interop bundle as immutable', () => {
        assert.equal(r('/signalk-onaplotter/js/leafletInterop.5QWJ3KXR.js'), 'immutable');
        // The stable re-export stays network-first so a deploy lands.
        assert.equal(r('/signalk-onaplotter/js/leafletInterop.js'), 'network-first');
        assert.equal(r('/signalk-onaplotter/js/platform/viewport.js'), 'network-first');
    });

    it('leaves unfingerprinted _framework loaders on the app-shell strategy', () => {
        assert.equal(r('/signalk-onaplotter/_framework/blazor.webassembly.js'), 'app-shell');
        assert.equal(r('/signalk-onaplotter/_framework/blazor.boot.json'), 'app-shell');
        assert.equal(r('/signalk-onaplotter/_framework/Microsoft.AspNetCore.Components.wasm'), 'app-shell');
    });

    it('routes SignalK API and chart tiles by absolute path', () => {
        assert.equal(r('/signalk/v1/api/vessels/self'), 'signalk');
        assert.equal(r('/signalk'), 'signalk');
        assert.equal(r('/signalk/chart-tiles/noaa/12/1/2.png'), 'tiles');
    });

    it('does not mistake the webapp prefix for the SignalK API', () => {
        assert.notEqual(r('/signalk-onaplotter/favicon.svg'), 'signalk');
        assert.equal(r('/signalk-onaplotter/favicon.svg'), 'app-shell');
    });

    it('does not treat another webapp\'s js/ as ours', () => {
        assert.equal(r('/other-app/js/main.js'), 'app-shell');
    });
});

describe('routeFor at the root scope', () => {
    const r = (p) => root.routeFor(p, root.scopePath);

    it('matches the standalone layout', () => {
        assert.equal(r('/'), 'network-first');
        assert.equal(r('/js/leafletInterop.js'), 'network-first');
        assert.equal(r('/css/app.css'), 'network-first');
        assert.equal(r('/_framework/dotnet.native.l9w6gmu0b1.wasm'), 'immutable');
        assert.equal(r('/icon-192.png'), 'app-shell');
    });
});

describe('service worker wiring', () => {
    it('registers install, activate and fetch handlers', () => {
        assert.equal(typeof sub.listeners.install, 'function');
        assert.equal(typeof sub.listeners.activate, 'function');
        assert.equal(typeof sub.listeners.fetch, 'function');
    });
});

describe('navigations', () => {
    it('route client routes under the subpath to the index fallback', () => {
        // A reload on the chart page must not hit the app-shell cache
        // (never precached: blank 504 offline, stale index after deploy).
        const r = (p) => sub.routeFor(p, sub.scopePath, 'navigate');
        assert.equal(r('/signalk-onaplotter/map'), 'navigate');
        assert.equal(r('/signalk-onaplotter/settings/display'), 'navigate');
        assert.equal(r('/signalk-onaplotter/'), 'navigate');
    });

    it('route root-scope client routes the same way', () => {
        assert.equal(root.routeFor('/map', root.scopePath, 'navigate'), 'navigate');
    });

    it('leave navigations outside the scope alone', () => {
        assert.equal(sub.routeFor('/admin/', sub.scopePath, 'navigate'), 'app-shell');
        assert.equal(sub.routeFor('/signalk/v1/api/', sub.scopePath, 'navigate'), 'signalk');
    });

    it('keep subresource routing unchanged', () => {
        assert.equal(sub.routeFor('/signalk-onaplotter/map', sub.scopePath, 'cors'), 'app-shell');
        assert.equal(sub.routeFor('/signalk-onaplotter/js/a.js', sub.scopePath, 'cors'), 'network-first');
    });
});

describe('fetch handler', () => {
    function dispatch(path, mode = 'cors') {
        let responded = false;
        sub.listeners.fetch({
            request: { url: `http://boat.local:3000${path}`, method: 'GET', mode },
            respondWith: () => { responded = true; },
        });
        return responded;
    }

    it('does not intercept SignalK API requests', () => {
        assert.equal(dispatch('/signalk/v1/api/vessels/self'), false);
    });

    it('intercepts navigations and shell assets', () => {
        sub.context.fetch = () => new Promise(() => {});
        sub.context.caches = { match: async () => undefined, open: async () => ({}) };
        sub.context.setTimeout = () => 0;
        sub.context.clearTimeout = () => {};
        sub.context.AbortController = AbortController;
        assert.equal(dispatch('/signalk-onaplotter/map', 'navigate'), true);
        assert.equal(dispatch('/signalk-onaplotter/js/leafletInterop.js'), true);
    });
});

describe('tile cache trim', () => {
    // Cache stand-in that counts full enumerations.
    function fakeCache(n) {
        const keys = Array.from({ length: n }, (_, i) => `t${i}`);
        return {
            enumerations: 0,
            keys: async function () { this.enumerations++; return [...keys]; },
            delete: async (k) => { keys.splice(keys.indexOf(k), 1); return true; },
            put(k) { keys.push(k); },
            get size() { return keys.length; },
        };
    }

    it('enumerates once to seed the count, then only past the cap', async () => {
        const w = loadWorker('http://localhost:5282/');
        const cache = fakeCache(10);
        cache.put('a');
        await w.context.maybeTrimTileCache(cache);
        assert.equal(cache.enumerations, 1);
        for (let i = 0; i < 100; i++) {
            cache.put(`b${i}`);
            await w.context.maybeTrimTileCache(cache);
        }
        assert.equal(cache.enumerations, 1, 'no walk while under the cap');
    });

    it('evicts the oldest down to the low-water mark when over the cap', async () => {
        const w = loadWorker('http://localhost:5282/');
        const max = w.context.TILE_CACHE_MAX_ENTRIES ?? 5000;
        const cache = fakeCache(max);
        cache.put('new');
        await w.context.maybeTrimTileCache(cache);
        assert.equal(cache.size, max - 250);
        // The next puts stay under the cap without another walk.
        const walks = cache.enumerations;
        for (let i = 0; i < 200; i++) {
            cache.put(`c${i}`);
            await w.context.maybeTrimTileCache(cache);
        }
        assert.equal(cache.enumerations, walks);
    });
});

describe('install', () => {
    it('precaches the render-blocking stylesheets and the HUD font', async () => {
        const w = loadWorker('http://boat.local:3000/signalk-onaplotter/');
        let urls = null;
        w.context.caches = { open: async () => ({ addAll: async (u) => { urls = u; } }) };
        w.context.self.skipWaiting = () => {};
        let done;
        w.listeners.install({ waitUntil: (p) => { done = p; } });
        await done;
        for (const path of ['', 'css/app.css', 'css/bootstrap/bootstrap.min.css',
                            'fonts/InterVariable.woff2', 'lib/leaflet/dist/leaflet.js']) {
            assert.ok(urls.includes(`http://boat.local:3000/signalk-onaplotter/${path}`), path);
        }
    });
});
