// Service worker for OnaPlotter PWA.
// Caches the app shell for faster loads; network-first for API calls.

// Bumped v2 -> v3 on the chartOverzoom module extraction so a browser
// holding the older cache doesn't serve a stale leafletInterop.js that
// still has the inline overzoom code but tries to import from a
// chartOverzoom.js module that wasn't cached.
const CACHE_NAME = 'ona-plotter-v3';
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
            Promise.all(keys.filter((k) => k !== CACHE_NAME).map((k) => caches.delete(k)))
        )
    );
    self.clients.claim();
});

self.addEventListener('fetch', (event) => {
    const url = new URL(event.request.url);

    // Only handle GET requests for same-origin resources.
    // Cross-origin (OSM tiles, OpenSeaMap, unpkg.com, MarineTraffic links) must pass
    // through untouched: the browser sends proper Referer headers that some servers
    // (like OSM) require per their usage policy. Intercepting them breaks those requests.
    if (event.request.method !== 'GET' || url.origin !== self.location.origin) {
        return;
    }

    // Network-first for SignalK API calls and WebSocket upgrades.
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
