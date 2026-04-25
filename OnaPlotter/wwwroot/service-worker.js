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
// v18 -> v19: scoped main-as-scroll-container fix for Settings /
//             Dashboard / History / Resources in fullscreen. Just
//             constrain <main> itself (height 100dvh, overflow-y
//             auto); leaves html / body alone so the chain that
//             tripped Firefox in v17 isn't reintroduced.
// v19 -> v20: outer-try guard on tileCacheFirst: any unhandled
//             throw / sync-rejection from the cache-API path now
//             falls through to a 504 placeholder rather than
//             rejecting respondWith(). Helm reported recurring
//             "ServiceWorker intercepted ... unexpected error" on
//             Firefox; v6 hardened the documented failure modes,
//             v20 catches the residue (clone-throw on quirky
//             Response bodies, sync-throws from cache.put on low-
//             memory Firefox, ...). Also drops the LRU clone+put
//             promotion on cache hits -- helm reported "no single
//             tile loads" which points at body-stream contention
//             between the served response and the background put.
//             We accept FIFO eviction and ship reliable tile
//             rendering; LRU was nice-to-have, tile delivery is
//             not negotiable.
const CACHE_NAME = 'ona-plotter-v20';
const TILE_CACHE_NAME = 'ona-plotter-tiles-v1';
// Cap on the tile cache. Approx 5000 tiles * ~40 kB = 200 MB which
// is comfortable on iPad / desktop and fits one or two full route-
// planning sessions. Eviction is FIFO by insertion order
// (trimTileCache below deletes from the front of cache.keys()); the
// earlier LRU promotion on hits was dropped after helm reports of
// "no single tile loads" pointed at body-stream contention between
// the served response and the background re-put. With a 5000 entry
// cap, FIFO is plenty for typical coastal / rivers use -- the home
// anchorage tiles fall out only after several sessions of heavy
// route-planning elsewhere. Bump this number rather than switching
// to a separate offline-tiles feature.
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

// Cache-first for tile URLs. Wraps the inner cache-then-fetch logic
// in a top-level try/catch that always returns *some* Response, so
// the respondWith() promise never rejects. A bare rejection is what
// Firefox surfaces as "ServiceWorker intercepted the request and
// encountered an unexpected error" and renders as a broken tile in
// Leaflet -- the cause is hard to pin (Cache-API quota, transient
// IndexedDB corruption, body-clone on a partial 206, ...) but the
// effect is uniform and so is the mitigation: any exception path,
// however unlikely, falls back to a 504 placeholder.
async function tileCacheFirst(request) {
    try {
        return await tileCacheFirstInner(request);
    } catch (_) {
        // Last-resort fallback: a 504 placeholder so Leaflet can
        // render its errorTileUrl rather than the helm seeing the
        // generic Firefox SW-intercept error in the console plus a
        // missing tile. This branch should be unreachable -- the
        // inner function has its own per-step guards -- but the
        // outer net is what guarantees respondWith() never rejects.
        return new Response('', { status: 504, statusText: 'Tile error' });
    }
}

async function tileCacheFirstInner(request) {
    let cache = null;
    try {
        cache = await caches.open(TILE_CACHE_NAME);
        const cached = await cache.match(request);
        if (cached) {
            // Just serve the cached response. Earlier versions did an
            // LRU promotion via cache.put(request, cached.clone()) so
            // that trimTileCache evicted least-recently-used entries.
            // Helm reports of "no single tile loads" point at the
            // clone path: in some browser builds (Firefox in
            // particular) the clone's body stream and the served
            // response share underlying state in a way that the
            // background put can lock, breaking every subsequent
            // tile read. The pure-FIFO eviction we land on without
            // promotion is good enough -- with a 5000-tile cap a
            // home anchorage stays in the cache for several
            // sessions of normal use, and the alternative
            // (every-tile failure) is not a trade we'd accept.
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
            // Defensive: cache.put can throw synchronously on some
            // browser versions (Firefox under low-memory, Safari with
            // partial-storage quotas). The .catch chains the async
            // rejection; the try/catch traps the sync throw so the
            // outer respondWith path stays clean.
            try {
                cache.put(request, clone)
                    .then(() => trimTileCache(cache).catch(() => { /* trim is best-effort */ }))
                    .catch(() => { /* put rejected; tile not cached, fine */ });
            } catch (_) { /* sync throw from cache.put */ }
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
