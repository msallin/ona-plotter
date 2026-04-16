// Service worker for OnaPlotter PWA.
// Caches the app shell for faster loads; network-first for API calls.

const CACHE_NAME = 'ona-plotter-v1';
const APP_SHELL = [
    '/',
    '/css/app.css',
    '/favicon.svg',
    '/manifest.json'
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

    // Network-first for API calls and WebSocket upgrades.
    if (url.pathname.startsWith('/signalk') || event.request.mode === 'websocket') {
        event.respondWith(fetch(event.request));
        return;
    }

    // Cache-first for app shell, network-first for everything else.
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
