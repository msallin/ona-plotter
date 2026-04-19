// Service worker for OnaPlotter PWA.
// Caches the app shell for faster loads; network-first for API calls.

// v2 -> v3: chartOverzoom module extraction (shape change).
// v3 -> v4: chart-tile cache-as-you-view (new rule below).
const CACHE_NAME = 'ona-plotter-v4';
const TILE_CACHE_NAME = 'ona-plotter-tiles-v1';
// Cap on the tile cache so a passage up the coast doesn't fill disk.
// Approx 1000 tiles * ~40kb = 40 MB. Rolled FIFO: when we exceed the
// cap, oldest entries are evicted. Blue-water cruisers with offline
// needs should manually pre-fetch a route (separate feature, TBD).
const TILE_CACHE_MAX_ENTRIES = 1000;
// Use relative URLs so the worker works both at root and under a subpath
// (SignalK webapp serves at /signalk-onaplotter/).
const SCOPE = self.registration ? self.registration.scope : self.location.href;
const APP_SHELL = [
    SCOPE,
    new URL('css/app.css', SCOPE).toString(),
    new URL('favicon.svg', SCOPE).toString(),
    new URL('manifest.json', SCOPE).toString()
];

self.addEventListener('install', (event) => {
    event.waitUntil(
        caches.open(CACHE_NAME).then((cache) => cache.addAll(APP_SHELL))
    );
    self.skipWaiting();
});

self.addEventListener('activate', (event) => {
    event.waitUntil(
        caches.keys().then((keys) =>
            Promise.all(keys
                .filter((k) => k !== CACHE_NAME && k !== TILE_CACHE_NAME)
                .map((k) => caches.delete(k)))
        )
    );
    self.clients.claim();
});

// Cache-first for tile URLs: respond from cache if present, otherwise
// go to network, store the response, and return it. Enforces a rolling
// cap so the cache doesn't grow unbounded across multi-week passages.
async function tileCacheFirst(request) {
    const cache = await caches.open(TILE_CACHE_NAME);
    const cached = await cache.match(request);
    if (cached) return cached;
    try {
        const response = await fetch(request);
        if (response.ok) {
            // clone() needs to happen BEFORE returning so both the cache
            // and the caller get a fresh body.
            cache.put(request, response.clone()).then(() => trimTileCache(cache));
        }
        return response;
    } catch (err) {
        // Offline + no cache entry. Return a harmless 504 so Leaflet
        // shows its errorTileUrl placeholder rather than hanging.
        return new Response('', { status: 504, statusText: 'Offline' });
    }
}

async function trimTileCache(cache) {
    const keys = await cache.keys();
    if (keys.length <= TILE_CACHE_MAX_ENTRIES) return;
    // Delete oldest (FIFO by insertion order). Cache.keys() returns in
    // insertion order per the spec; sufficient for our approximate eviction.
    const excess = keys.length - TILE_CACHE_MAX_ENTRIES;
    for (let i = 0; i < excess; i++) await cache.delete(keys[i]);
}

self.addEventListener('fetch', (event) => {
    const url = new URL(event.request.url);

    // Only handle GET requests for same-origin resources.
    // Cross-origin (OSM tiles, OpenSeaMap, unpkg.com, MarineTraffic links) must pass
    // through untouched: the browser sends proper Referer headers that some servers
    // (like OSM) require per their usage policy. Intercepting them breaks those requests.
    if (event.request.method !== 'GET' || url.origin !== self.location.origin) {
        return;
    }

    // Chart tiles served by signalk-charts-plugin. Cache-on-view so a
    // re-visit to a cove the user sailed through earlier works offline.
    // Separate cache from the app shell so it can be evicted
    // independently and capped by entry count. Network-first keeps the
    // freshest tiles when online; cache-fallback kicks in on wifi loss.
    if (url.pathname.startsWith('/signalk/chart-tiles/')) {
        event.respondWith(tileCacheFirst(event.request));
        return;
    }

    // Network-first for other SignalK API calls and WebSocket upgrades.
    // Match /signalk/ and /signalk/v* paths exactly (not webapp names like /signalk-onaplotter).
    if (url.pathname === '/signalk' || url.pathname.startsWith('/signalk/')
        || event.request.mode === 'websocket') {
        event.respondWith(fetch(event.request));
        return;
    }

    // Cache-first for app shell, network fallback otherwise.
    event.respondWith(
        caches.match(event.request).then((cached) => {
            const fetchPromise = fetch(event.request).then((response) => {
                if (response.ok) {
                    const clone = response.clone();
                    caches.open(CACHE_NAME).then((cache) => cache.put(event.request, clone));
                }
                return response;
            }).catch(() => cached);
            return cached || fetchPromise;
        })
    );
});
