// Service worker for OnaPlotter PWA.
// Caches the app shell for faster loads; network-first for API calls.

// v2 -> v3: chartOverzoom module extraction (shape change).
// v3 -> v4: chart-tile cache-as-you-view (new rule below).
// v4 -> v5: errorRelayBoot.js + themeApply.js + index.html shell update;
//           bundle precaches the new boot script so an iPad that
//           loaded an older version doesn't keep serving it offline.
//           Bump CACHE_NAME on EVERY shell-asset change going forward
//           (favicon, css, html, any APP_SHELL entry).
// v5 -> v6: harden tileCacheFirst against Cache API rejections so
//           Firefox no longer surfaces tile fetches as
//           "ServiceWorker intercepted ... unexpected error".
// v6 -> v7: PWA standalone scrollbar fix (sidebar 100dvh + body
//           overflow lock) + sidebar-collapse user toggle. CSS
//           changes only, but APP_SHELL precaches need a refresh.
// v7 -> v8: anchor-on-route-activate JS marker clear (race fix)
//           + in-place route edit forces active-route refetch.
//           Bundled C# changes won't affect SW behaviour but the
//           helm should see the fixed flow on the next reload, so
//           bump triggers an app shell refresh.
// v8 -> v9: sidebar-collapse moved into NavMenu foot; iOS fullbleed
//           map-container fills full viewport (no -3rem topbar gap)
//           so Safari's bottom toolbar no longer overlaps the HUD.
// v9 -> v10: anchor-raise visual feedback (dim marker until SK delta
//            confirms) replaces the optimistic clear that masked
//            errors and raced the next sync tick.
// v10 -> v11: wheel zoom + arrow-step + reverse + save-as-copy in
//             route edit; Stop Navigation now uses the same dim-and-
//             wait pattern as anchor raise instead of optimistically
//             clearing local state.
// v11 -> v12: route activation no longer auto-raises the anchor (only
//             the reverse direction stays); editing the active route
//             temporarily hides the active overlay to avoid double-
//             drawing, restores after Cancel/Save.
// v12 -> v13: tile cache cap 1000 -> 5000 + LRU promotion on hit
//             (delete+re-put moves entries to end of insertion order
//             so trim evicts least-recently-used). keepBuffer 4 -> 6
//             on tile layers for snappier route-planning pans.
// v13 -> v14: post-sail-readiness follow-ups -- sidebar-collapse
//             driven by user setting only (no longer forced by
//             :fullscreen / .ios-fullbleed CSS so the chevron toggle
//             works in fullscreen); active-route hides the regular
//             polyline so the dashed-leg overlay isn't double-drawn;
//             route HUD shows passed/total nm; iPad follow centres
//             the boat above the geometric centre; CPA dismiss
//             cooldown 30s -> 15min; PNG PWA icons + apple-touch-icon
//             so iPad install shows the OnaPlotter logo; prev/next
//             waypoint buttons in route HUD; AIS popup autoPan off.
// v14 -> v15: real-browser :fullscreen now hides the topbar (matches
//             .ios-fullbleed) so the 100dvh map-container fits the
//             viewport without 3rem of bottom overflow on Firefox /
//             Chrome / Edge; Settings + other overflowing pages get
//             a main-as-scroll-container fallback under :fullscreen
//             so the helm can scroll Settings while in real
//             fullscreen.
// v15 -> v16: branded boot loading screen (favicon-derived SVG +
//             spinner + build stamp from js/version.g.js); phone
//             first-run default collapses the sidebar to the icon
//             rail; nav menu picks up a tighter @media trim under
//             600px so labels + icons fit without crowding.
// v16 -> v17: drop the body { overflow: hidden } chain we added under
//             :fullscreen for non-map pages. On Firefox the chain
//             ate Settings / History / Resources scroll instead of
//             routing it to <main>; reverting to the natural document
//             scroll restores it. Map page is still locked via the
//             body:has(.map-container) rule.
// v17 -> v18: stop hiding the topbar in :fullscreen / .ios-fullbleed
//             so zoom / night / liveness / exit-fullscreen affordances
//             stay visible while fullscreen. Map-container falls back
//             to its non-fullscreen calc(100dvh - 3rem) height. Also
//             explicit pointer-events: auto on the route prev/next
//             and autopilot buttons inside the non-corner HUDs (the
//             parent .hud / .hud-panel pointer-events: none was
//             swallowing taps on those new buttons).
const CACHE_NAME = 'ona-plotter-v18';
const TILE_CACHE_NAME = 'ona-plotter-tiles-v1';
// Cap on the tile cache. Approx 5000 tiles * ~40 kB = 200 MB which
// is comfortable on iPad / desktop and fits one or two full route-
// planning sessions. Eviction is LRU (see tileCacheFirst): on every
// cache hit we delete + re-put the entry, which moves it to the end
// of the cache's insertion order. trimTileCache below evicts from
// the start, so the LEAST-recently-used tiles are dropped first --
// the home anchorage tiles you visit every session survive across
// sessions even when the cache rolls. Bump this number rather than
// switching to a separate offline-tiles feature for typical
// coastal / rivers users.
const TILE_CACHE_MAX_ENTRIES = 5000;
// Use relative URLs so the worker works both at root and under a subpath
// (SignalK webapp serves at /signalk-onaplotter/).
const SCOPE = self.registration ? self.registration.scope : self.location.href;
const APP_SHELL = [
    SCOPE,
    new URL('css/app.css', SCOPE).toString(),
    new URL('favicon.svg', SCOPE).toString(),
    new URL('manifest.json', SCOPE).toString(),
    // errorRelayBoot.js is loaded synchronously from index.html before
    // the Blazor runtime so the relay listeners are armed in time to
    // catch boot-time exceptions. Precaching it here means a stale
    // copy is invalidated cleanly when CACHE_NAME bumps; without it
    // the catch-all opportunistic cache could pin an old broken copy.
    new URL('js/errorRelayBoot.js', SCOPE).toString(),
    // PNG icons for the iPad / iOS home-screen install path. The SVG
    // is still listed as a manifest icon (Android handles SVG fine)
    // but Safari needs the rasterised versions for apple-touch-icon
    // and PWA splash; precache so the install flow works offline.
    new URL('apple-touch-icon-180.png', SCOPE).toString(),
    new URL('icon-192.png', SCOPE).toString(),
    new URL('icon-512.png', SCOPE).toString()
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
//
// Defensive shape: every Cache API call is wrapped so an unexpected
// failure (storage quota exceeded, corrupted index, partial-content
// responses that cache.put refuses, no-store headers, etc.) falls
// through to a plain network fetch rather than rejecting the
// respondWith() promise -- which Firefox surfaces as
// "ServiceWorker intercepted the request and encountered an
// unexpected error" and renders as a broken tile in Leaflet.
async function tileCacheFirst(request) {
    let cache = null;
    try {
        cache = await caches.open(TILE_CACHE_NAME);
        const cached = await cache.match(request);
        if (cached) {
            // LRU promotion: re-insert this hit so it moves to the
            // end of the cache's insertion order. trimTileCache below
            // evicts FIFO from the start, so this turns the cap into
            // an effective LRU policy without needing a sidecar
            // IndexedDB index.
            //
            // Just `cache.put` -- not delete-then-put. The Cache spec
            // says put atomically replaces an existing entry with
            // the same Request key, and major engines (Chromium /
            // WebKit / Gecko) move the replacement to the end of
            // insertion order. Avoiding the explicit delete also
            // closes the race window where a sibling fetch for the
            // same tile during the gap would miss the cache and
            // trigger a redundant network round-trip.
            //
            // Clone BEFORE returning -- Response bodies are single-
            // use streams and the caller (event.respondWith) will
            // start consuming the original immediately.
            const promote = cached.clone();
            cache.put(request, promote).catch(() => { /* best-effort */ });
            return cached;
        }
    } catch (_) {
        // Cache layer unavailable; degrade to a pure-network path
        // below. cache stays null, the put attempt is skipped.
    }

    let response;
    try {
        response = await fetch(request);
    } catch (_) {
        // Offline + no cache entry. Return a harmless 504 so Leaflet
        // shows its errorTileUrl placeholder rather than hanging.
        return new Response('', { status: 504, statusText: 'Offline' });
    }

    // Best-effort cache write. cache.put rejects on partial-content
    // (206), no-store headers, quota exceeded, and a few other Response
    // shapes that the spec disallows. None of those should affect the
    // caller -- swallow the rejection and just return the response.
    if (cache && response.ok) {
        try {
            const clone = response.clone();
            cache.put(request, clone)
                .then(() => trimTileCache(cache).catch(() => { /* trim is best-effort */ }))
                .catch(() => { /* put rejected; tile not cached, fine */ });
        } catch (_) { /* clone() threw on a weird response body */ }
    }
    return response;
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
