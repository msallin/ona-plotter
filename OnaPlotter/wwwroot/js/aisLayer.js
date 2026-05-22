// AIS / radar / SART target rendering. Owns:
//   * Per-vessel marker (chevron / radar triangle / SART pulsing
//     bullseye), name label, COG vector, and rolling 60 s trail.
//   * CPA crossing-situation overlay: lines from each vessel to its
//     predicted CPA point + a labelled chip at the midpoint.
//   * Guard zone ring around own boat (CPA alarm radius).
//   * Harbor mode declutter: drops name labels and hides the guard
//     ring without losing per-context state. COG vectors + CPA
//     crossing lines stay - moored vessels are filtered out C#-side
//     by HarborAisFilter, so every target still on screen is moving
//     and the helm needs to see where it's heading + any closing
//     geometry. Audio CPA alarm is suppressed by CpaAlarmRule on
//     the C# side; only the visual stays.
// Visual fields (ship-type palette, glyph category, SART category,
// CPA threat band) are resolved on the C# side; JS just draws them.

import { DEG, NM_PER_METER, haversineMeters, bearingDeg } from './geoMath.js';
import { MS_TO_KNOTS, stalenessOpacity, rangeRingLabel } from './format.js';
import { esc } from './popupHelpers.js';

let mapRef = null;
let colors = null;
let isSlowClient = false;
let getDotNetRef = null;
let getEditModeFlags = null;       // () => { routeEdit, polygonEdit, measure }
let editModeAddPoint = null;       // (mode, lat, lon) -> void
let getOwnMmsi = null;
let flagUrl = null;
let rotateMarker = null;

// Per-vessel rendered state. Keys are SignalK contexts.
// aisMarkers is a Map (not a plain object) because the stale-sweep
// loop at end-of-tick iterates over it. A Map yields its entries
// without materialising an Object.keys() array; on a 200-vessel
// harbour push that's one fewer N-string allocation per 3 s tick.
// The sibling layer dicts below stay plain objects - they're never
// iterated on the hot path, only key-accessed, so a Map gives no
// measurable win.
const aisMarkers = new Map();
const aisVectors = {};
// Tip-of-vector dot. Reads as "this is where the boat will be in
// VECTOR_MINUTES" - without it the line just trails off and the
// helm has to mentally extrapolate the endpoint.
const aisVectorTips = {};
// CPA overlay state per vessel context. The full overlay is five
// elements painted together as one closing-encounter picture:
//   * aisCpaOwnLines    - dashed line from own boat to own's
//                         projected CPA point.
//   * aisCpaOwnX        - X marker at own's projected CPA point.
//   * aisCpaTgtLines    - dashed line from target to its projected
//                         CPA point.
//   * aisCpaTgtX        - X marker at the target's projected CPA point.
//   * aisCpaSegments    - line between the two X markers (the actual
//                         closest-approach distance segment); carries
//                         the "name / cpa nm / tcpa min" label at its
//                         midpoint via a permanent tooltip for alarm-
//                         tier targets, hover-only for awareness.
// The whole overlay paints together or not at all per vessel; the
// gate is: alarm tier always shows, awareness tier shows only when
// the helm has click-selected the vessel (see _selectedAwarenessCtx
// below).
const aisCpaOwnLines = {};
const aisCpaOwnX = {};
const aisCpaTgtLines = {};
const aisCpaTgtX = {};
const aisCpaSegments = {};
// Last-seen severity per target. Currently only consulted by
// removeCpaOverlay to drop the entry on de-classification; future
// uses (transition-driven UI) can hang off the same map.
const aisCpaLastSeverity = {};

// Helm-click selection for awareness-tier overlays. Alarm-tier
// targets always render their full overlay; awareness targets
// render it ONLY when the helm has clicked them (toggle). Clearing
// happens by clicking the selected target a second time, or by
// clicking a different awareness target (the selection moves).
// A null value means "no awareness vessel currently selected".
let _selectedAwarenessCtx = null;
// Trail history (per-vessel sliding window of recent positions) lives
// in C# now (Services/Map/AisTrailBuffer + AisPushService.FillPayload).
// JS only keeps the rendered polyline reference; the C# side ships
// fresh coords on v.trail when it changed, otherwise we leave the
// existing line alone.
const aisTrailLines = {};
const aisLabels = {};

// Best-effort external-lookup cache for vessels whose SignalK feed
// hasn't yet delivered a static-data AIS message (message 5 / 24).
// Keyed by MMSI. A value of null means "looked up and came back
// empty" - prevents endless retries. Bounded: on insert past
// VESSEL_NAME_CACHE_MAX we drop the oldest entry. A Map is used
// because its iteration is insertion-ordered, so the first key is
// the oldest, which is all we need for a simple LRU with promote-
// on-hit.
const VESSEL_NAME_CACHE_MAX = 500;
const vesselNameCache = new Map();

// Country-flag cache. Two layers keyed by MMSI:
//   * flagPromiseCache (mmsi -> Promise<dataUri | null>) - dedupes
//     in-flight fetches so two popup-opens for the same vessel in
//     quick succession share one network round-trip.
//   * flagSettledCache (mmsi -> dataUri | null) - populated when
//     the promise resolves. Subsequent popup builds read this
//     synchronously and embed the data URI directly, so a second
//     open for a vessel never hits the network at all (and doesn't
//     depend on the SK server's HTTP cache headers, which the
//     signalk-flags plugin doesn't always set).
//
// Lazy-only: no eager prewarm on updateAisTargets. In a busy
// harbour with 200 AIS targets, ~95% of popups never open; the
// pre-warm wasted 200 round-trips per session for the median helm.
// First popup-open for an MMSI now triggers exactly one fetch;
// every subsequent open for the same MMSI is fed from the cache.
//
// LRU cap: each settled data URI is ~3-10 KB. Without a cap the
// cache grew to ~MB-class on a long passage through busy waters.
// 200 entries is well above the simultaneous-popup-history any
// helm cycles through (the chip stack only surfaces ~10 vessels
// at a time); least-recently-USED entries (by popup-build read,
// not by fetch order) are evicted first via Map's insertion-order
// iteration.
const FLAG_CACHE_MAX = 200;
const flagPromiseCache = new Map();
const flagSettledCache = new Map();
let flagPlaceholderSeq = 0;

/** Re-insert key=>value to bump it to most-recent in Map's
 *  insertion-order iteration. No-op when the key isn't present.
 *  Skips the delete+set when the cache isn't near its cap - the
 *  delete+set pair triggers V8 internal-bucket rebalancing on the
 *  Map, which on hot paths (per-vessel cache reads at 200×0.33 Hz
 *  for vessel-name + flag lookups) adds up. Once we're within 75 %
 *  of the cap the eviction order matters again. */
function lruBump(map, key, max) {
    if (!map.has(key)) return;
    if (max !== undefined && map.size < max * 0.75) return;
    const v = map.get(key);
    map.delete(key);
    map.set(key, v);
}

/** Set + cap. Evicts oldest entries until size <= max. */
function lruSet(map, key, value, max) {
    if (map.has(key)) map.delete(key);
    map.set(key, value);
    while (map.size > max) {
        const oldest = map.keys().next().value;
        map.delete(oldest);
    }
}

// Icon caches (one per source x colour x category combo).
const aisIconCache = {};
const radarIconCache = {};
const sartIconCache = {};
// AIS icon size. 28 leaves own boat (30) visibly bigger while making
// other traffic actually legible at chart zoom. 24 read as "too
// small" on a helm screen, especially with a ship-type glyph
// overlaid - the glyph shrank to noise.
const AIS_ICON_SIZE = 28;
const RADAR_ICON_SIZE = 26;

// Own vessel state, pushed by the mux on every updatePosition tick.
let selfLat = 0, selfLon = 0, selfCogRad = null, selfSogMs = null;

// Helm-configured distance rings on own boat. Pure visual
// scaffolding ("how far is that pier?") with no alarm semantics
// and no link to the CPA / TCPA pipeline. Drawn as thin grey
// dashed circles so they read as a measurement tool rather than
// an alarm boundary. The Nth ring sits at baseNm × N, so the
// default 0.5 nm × 4 setup draws rings at 0.5 / 1.0 / 1.5 /
// 2.0 nm. Independent of harbor mode (helm-controlled) and of
// the guard-zone visibility flags. Default disabled - opt-in
// from Settings -> Display.
let distanceRingsEnabled = false;
let distanceRingsBaseNm = 0.5;
let distanceRingsCount = 4;
// Parallel arrays: distanceRings[i] is the L.circle, ...Labels[i] is
// the L.tooltip at its top. Length matches distanceRingsCount once
// drawDistanceRings has settled; the per-tick path in setBoatPosition
// reads .length so a partial mid-resize state is safe.
let distanceRings = [];
let distanceRingLabels = [];
// Pre-computed lat-degree north-offset per ring. Same trick as the
// guard-ring labels: divide ONCE on radius change, then per-tick
// repositioning is `lat + _distanceRingLatDegs[i]` - no division
// inside the boat-position hot path.
let _distanceRingLatDegs = [];

// Harbor-mode flag. When true the AIS render path skips name labels,
// COG vectors, and CPA overlays, the guard-zone ring is not drawn,
// and moored vessels are filtered upstream in C#.
let harborMode = false;

// Persistent helm preference for AIS vessel name labels (independent
// of harbor mode). Default true matches existing behaviour. The label-
// render gate ANDs this with !harborMode so harbor mode still hides
// labels while it's on, and turning harbor mode off doesn't resurrect
// labels the helm hid via Settings.
let aisLabelsVisible = true;

// Helm-configured "AIS inactive" threshold (in seconds). Targets whose
// payload.ageSec equals or exceeds this are treated as stale: the marker
// is pinned at the staleness-floor opacity AND the name label is
// suppressed so a chart full of ghost vessels stays legible. Mirrors
// IMapDisplaySettings.AisInactiveMinutes; default 300 s (5 min) matches
// the AppSettings default until Map.razor pushes the live value at
// startup. Seconds (not minutes) for cheap per-vessel comparison against
// the C#-computed ageSec.
let aisInactiveSeconds = 300;

// Mirror of leafletInterop.followBoat. Kept here so the AIS popup-open
// path can pick a popup direction that opens AWAY from the nearest
// viewport edge instead of relying on Leaflet's autoPan, which would
// scroll the map and break follow-mode. leafletInterop is the source
// of truth; it pushes updates into setFollow() below whenever the helm
// toggles follow or a manual pan drops it.
let followBoat = true;

// Reusable [lat, lon] scratch passed to L.marker() / marker.setLatLng()
// per vessel per tick. Leaflet copies the values into its own LatLng
// internally so a single shared array is safe; eliminates ~200 fresh
// 2-element allocations per push on a busy-harbour AIS update.
const _scratchLatLng = [0, 0];

export function init(map, deps) {
    mapRef = map;
    colors = deps.colors;
    isSlowClient = deps.isSlowClient;
    getDotNetRef = deps.getDotNetRef;
    getEditModeFlags = deps.getEditModeFlags;
    editModeAddPoint = deps.editModeAddPoint;
    getOwnMmsi = deps.getOwnMmsi;
    flagUrl = deps.flagUrl;
    rotateMarker = deps.rotateMarker;

    // Custom pane for COG vectors so they stack ABOVE the CPA
    // crossing-situation lines. Both used to share Leaflet's default
    // overlayPane (zIndex 400) and rendered in creation order, which
    // meant the long red CPA line sat on top of the small forward
    // COG vector and obscured it. zIndex 410 is above overlayPane
    // and below markerPane (600 by default); CPA lines stay in the
    // overlayPane below. Created idempotently in case init runs
    // twice.
    if (!map.getPane('aisCogVectors')) {
        const pane = map.createPane('aisCogVectors');
        pane.style.zIndex = '410';
        // Pane events disabled - we don't want the pane element
        // intercepting clicks meant for the marker pane above.
        pane.style.pointerEvents = 'none';
    }
}

// Mux pushes the own-boat snapshot. CPA prediction needs SOG / COG
// in addition to lat / lon, and the distance rings (+ their
// north-of-boat distance labels) chase the boat.
export function setBoatPosition(lat, lon, cogRad, sogMs) {
    // Cache the COG/SOG snapshot even when position didn't change
    // (these can update independently between ticks - especially
    // on a vessel parked at anchor whose heading wanders). Only
    // the LatLng-bound visuals (rings + labels) get the position
    // guard below.
    selfCogRad = cogRad;
    selfSogMs = sogMs;
    if (selfLat === lat && selfLon === lon) return;
    selfLat = lat;
    selfLon = lon;
    // Distance rings + their labels. Cheap when the feature is off
    // (length 0). Per-tick path is one add per ring, no division -
    // the lat-degree offsets are precomputed once on radius change.
    for (let i = 0; i < distanceRings.length; i++) {
        distanceRings[i].setLatLng([lat, lon]);
        const lbl = distanceRingLabels[i];
        if (lbl) lbl.setLatLng([lat + _distanceRingLatDegs[i], lon]);
    }
}

// --- icons ---

function makeBoatSvg(fill, size, isOwn, category) {
    const s = size || 28;
    const h = s / 2;
    // Sleek arrow shape: pointed bow, tapered stern with notch.
    const outline = isOwn
        ? `stroke="#fff" stroke-width="1.2" stroke-linejoin="round"`
        : `stroke="rgba(255,255,255,0.5)" stroke-width="0.8" stroke-linejoin="round"`;
    return `<svg width="${s}" height="${s}" viewBox="-${h} -${h} ${s} ${s}" style="filter:drop-shadow(0 1px 2px rgba(0,0,0,0.4))">
              <polygon points="0,-${h-2} ${h-6},${h-4} 0,${h-8} -${h-6},${h-4}" fill="${fill}" ${outline} opacity="${isOwn ? 1 : 0.9}"/>
              ${shipTypeGlyph(category)}
            </svg>`;
}

// Small glyph overlaid near the centre of the chevron. Kept to 3-4 px
// so it never obscures the outline; stroke colour is a fixed dark so
// it reads on any coloured chevron (including muted warms). The
// category string comes from C# (Utilities/AisPalette.ShipTypeCategory);
// this function is a pure renderer that maps a category to SVG.
function shipTypeGlyph(category) {
    const stroke = `stroke="rgba(0,0,0,0.7)" stroke-width="0.9" stroke-linecap="round"`;
    switch (category) {
        case 'sail':       // small diamond
            return `<polygon points="0,-3 2.5,0 0,3 -2.5,0" fill="rgba(255,255,255,0.85)" ${stroke}/>`;
        case 'fish':       // crossed nets
            return `<line x1="-2.5" y1="-2" x2="2.5" y2="2" ${stroke}/>`
                 + `<line x1="-2.5" y1="2"  x2="2.5" y2="-2" ${stroke}/>`;
        case 'commercial': // solid dot (bulk / deck superstructure)
            return `<circle cx="0" cy="0.5" r="1.8" fill="rgba(0,0,0,0.75)"/>`;
        case 'service':    // plus
            return `<line x1="-2.5" y1="0" x2="2.5" y2="0" ${stroke}/>`
                 + `<line x1="0" y1="-2.5" x2="0" y2="2.5" ${stroke}/>`;
        default:
            return '';
    }
}

function makeIcon(html, size) {
    return L.divIcon({ className: 'boat-icon', html, iconSize: [size, size], iconAnchor: [size/2, size/2] });
}

function makeRadarSvg(fill, size) {
    const s = size || 22;
    const h = s / 2;
    return `<svg width="${s}" height="${s}" viewBox="-${h} -${h} ${s} ${s}" `
         + `style="filter:drop-shadow(0 1px 2px rgba(0,0,0,0.4))">`
         + `<polygon points="0,-${h-3} ${h-4},${h-5} -${h-4},${h-5}" `
         + `fill="none" stroke="${fill}" stroke-width="1.6" stroke-linejoin="round" opacity="0.95"/>`
         + `<circle cx="0" cy="0" r="1.6" fill="${fill}"/></svg>`;
}

function getAisIcon(color, category) {
    const key = `${color}|${category || ''}`;
    if (!aisIconCache[key]) {
        aisIconCache[key] = makeIcon(
            makeBoatSvg(color, AIS_ICON_SIZE, false, category), AIS_ICON_SIZE);
    }
    return aisIconCache[key];
}
function getRadarIcon(color) {
    if (!radarIconCache[color]) {
        radarIconCache[color] = makeIcon(
            makeRadarSvg(color, RADAR_ICON_SIZE), RADAR_ICON_SIZE);
    }
    return radarIconCache[color];
}

// SART / MOB / EPIRB marker: large pulsing red bullseye with the
// category label inside. Intentionally nothing like the chevron so
// it reads as "not a vessel, emergency transmitter" at a glance.
// The CSS .sart-icon class drives the pulse animation.
function getSartIcon(category) {
    if (!sartIconCache[category]) {
        const label = category;
        const size = 44;
        const html = `
            <div class="sart-icon sart-${label.toLowerCase()}">
                <svg width="${size}" height="${size}" viewBox="-22 -22 44 44">
                    <circle cx="0" cy="0" r="18" fill="rgba(239,68,68,0.18)"
                            stroke="#ef4444" stroke-width="2"/>
                    <circle cx="0" cy="0" r="10" fill="rgba(239,68,68,0.35)"
                            stroke="#ef4444" stroke-width="1.2"/>
                    <text x="0" y="3" fill="#fff" font-size="7" font-weight="700"
                          text-anchor="middle" style="letter-spacing:0.05em;"
                          paint-order="stroke" stroke="#7f1d1d" stroke-width="0.6">${label}</text>
                </svg>
            </div>`;
        sartIconCache[category] = L.divIcon({
            className: 'sart-icon-wrapper',
            html,
            iconSize: [size, size],
            iconAnchor: [size / 2, size / 2],
        });
    }
    return sartIconCache[category];
}

/**
 * Get a Promise<dataUri | null> for the country flag of an MMSI.
 * Dedupes in-flight fetches; on resolution, populates
 * flagSettledCache so subsequent popup builds read it synchronously.
 * Negative result (404 / network error) cached as null so we don't
 * keep retrying the same plugin-missing endpoint. Both caches are
 * LRU-capped at FLAG_CACHE_MAX entries.
 */
function getFlagDataUri(mmsi) {
    if (!mmsi) return Promise.resolve(null);
    let p = flagPromiseCache.get(mmsi);
    if (p) {
        lruBump(flagPromiseCache, mmsi, FLAG_CACHE_MAX);
        return p;
    }
    p = (async () => {
        try {
            const resp = await fetch(flagUrl(mmsi));
            if (!resp.ok) return null;
            const blob = await resp.blob();
            return await new Promise((resolve, reject) => {
                const fr = new FileReader();
                fr.onload = () => resolve(/** @type {string} */ (fr.result));
                fr.onerror = reject;
                fr.readAsDataURL(blob);
            });
        } catch {
            return null;
        }
    })();
    lruSet(flagPromiseCache, mmsi, p, FLAG_CACHE_MAX);
    p.then(value => lruSet(flagSettledCache, mmsi, value, FLAG_CACHE_MAX));
    return p;
}

/**
 * Build the popup-flag <img> HTML for an MMSI. When the flag is
 * already cached (second + opens for the same vessel) the data URI
 * is embedded directly - no network, no flicker. On first miss a
 * placeholder is emitted with a unique id; the async fetch fills
 * it (or hides it on 404) once the data URI lands.
 */
function flagImgHtml(mmsi) {
    if (!mmsi) return '';
    if (flagSettledCache.has(mmsi)) {
        const settled = flagSettledCache.get(mmsi);
        // Reading counts as "use" - bump the LRU position so a
        // helm cycling through the same handful of buddies doesn't
        // get them evicted by passing traffic.
        lruBump(flagSettledCache, mmsi, FLAG_CACHE_MAX);
        if (!settled) return '';   // negative-cached
        return `<img class="ais-popup-flag" src="${settled}" alt="">`;
    }
    // First miss: emit a placeholder, kick the fetch, fill on resolve.
    // visibility:hidden reserves the layout slot so the popup doesn't
    // reflow when the flag arrives.
    const id = `ais-flag-ph-${++flagPlaceholderSeq}`;
    queueMicrotask(() => {
        getFlagDataUri(mmsi).then(uri => {
            const el = document.getElementById(id);
            if (!el) return;
            if (uri) {
                el.src = uri;
                el.style.visibility = '';
            } else {
                el.style.display = 'none';
            }
        });
    });
    return `<img id="${id}" class="ais-popup-flag" alt="" style="visibility:hidden">`;
}

// --- vessel name cache ---

function vesselNameCacheGet(mmsi) {
    if (!vesselNameCache.has(mmsi)) return undefined;
    const v = vesselNameCache.get(mmsi);
    // Promote: re-insert at the end so a recently-used entry isn't next
    // to evict. Skip the delete+set when we're nowhere near the cap -
    // the rebalance is a measurable cost when 200 vessels poll this
    // every tick on a busy harbour. Once size approaches the cap the
    // eviction order matters and the promote re-engages.
    if (vesselNameCache.size >= VESSEL_NAME_CACHE_MAX * 0.75) {
        vesselNameCache.delete(mmsi);
        vesselNameCache.set(mmsi, v);
    }
    return v;
}

function vesselNameCacheHas(mmsi) { return vesselNameCache.has(mmsi); }

// Vessel-name enrichment is disabled. The previous implementation
// used api.allorigins.win as a CORS proxy to scrape vesselfinder.com,
// but that proxy itself stopped sending CORS headers and now floods
// the console. The SignalK server already receives AIS message
// type 5 (static data) for named vessels within minutes of first
// sighting, so the common scenario is "wait a bit and the name
// shows up". A proper long-term home for this lookup is a SignalK
// server-side plugin.
//
// resolveVesselName is kept as a no-op so call sites remain; the
// vesselNameCache is honoured for any future external injector that
// reaches in via the module reference.
async function resolveVesselName(_context, _mmsi) {
    return null;
}

// --- popup builder ---

/**
 * Resolves the popup-title string for an AIS vessel given the
 * available name signals. Pure function — no DOM, no module state —
 * so the precedence is testable from Node.
 *
 * Precedence (top wins):
 *   1. C# canonical `displayName` when SK delivered a real name.
 *      The canonical already has the buddy-star prefix baked in, so
 *      this branch returns it verbatim.
 *   2. `cachedName` (JS-only enrichment via external-lookup cache;
 *      only meaningful when SK gave us only an MMSI).
 *   3. `callsign` (last readable fallback before bare numerics).
 *   4. `MMSI <id>` — the "MMSI " prefix is JS-side disambiguation so
 *      a bare 9-digit number doesn't read as a coordinate or distance.
 *   5. `Unknown` — final fallback.
 *
 * Branches 2-5 add the `★ ` buddy prefix here since they don't go
 * through C#. The return value is RAW (un-escaped); callers that
 * inject into HTML must escape.
 */
export function resolveAisPopupTitle({ displayName, name, callsign, mmsi, buddy, cachedName }) {
    const star = buddy ? '★ ' : '';
    if (name && displayName) return displayName;
    if (cachedName) return star + cachedName;
    if (callsign) return star + callsign;
    if (mmsi) return star + 'MMSI ' + mmsi;
    return star + 'Unknown';
}

/**
 * Unpack a flat alternating [lat0, lon0, lat1, lon1, ...] array into
 * [[lat, lon], ...] tuples for Leaflet polyline/setLatLngs. Producer
 * is the C# AisTrailBuffer.GetCoords / AisVesselPayload.Trail wire
 * shape; length is expected even, a trailing odd element is silently
 * dropped.
 *
 * Input:  [54.0, 11.0, 54.1, 11.1, 54.2, 11.2]
 * Output: [[54.0, 11.0], [54.1, 11.1], [54.2, 11.2]]
 */
export function unpackLatLonPairs(flat) {
    const pairs = new Array(flat.length >> 1);
    for (let i = 0, j = 0; i + 1 < flat.length; i += 2, j++) {
        pairs[j] = [flat[i], flat[i + 1]];
    }
    return pairs;
}

/**
 * Builds the "Last update" row for the AIS popup table.
 *
 * Renders the C#-supplied LastAisSeen ISO instant as local 24h hh:mm,
 * with an "(N min ago)" tail computed from the C#-stamped ageSec so
 * both halves come off the same clock and stay consistent even after
 * a paused tab resumes. Falls back to "ageSec ago" alone when the
 * wire field is missing (defensive - newer payloads always populate
 * it but a transient interop hiccup shouldn't drop the row).
 *
 * Input:
 *   lastAisSeenIso "2026-05-17T11:42:13Z"
 *   ageSec         183
 * Output:
 *   <tr><td>Last</td><td>13:42 (3 min ago)</td></tr>
 */
function formatLastAisSeenRow(lastAisSeenIso, ageSec) {
    let hhmm = null;
    if (typeof lastAisSeenIso === 'string' && lastAisSeenIso.length > 0) {
        const d = new Date(lastAisSeenIso);
        if (!isNaN(d.getTime())) {
            const h = String(d.getHours()).padStart(2, '0');
            const m = String(d.getMinutes()).padStart(2, '0');
            hhmm = `${h}:${m}`;
        }
    }
    const ago = formatAgo(ageSec);
    if (!hhmm && !ago) return '';
    let cell;
    if (hhmm && ago) cell = `${hhmm} (${ago})`;
    else cell = hhmm ?? ago;
    return `<tr><td>Last</td><td>${cell}</td></tr>`;
}

/**
 * Renders a non-negative duration in seconds as "Ns" / "N min ago" /
 * "Hh Mm ago". Returns null when ageSec is missing or negative (clock
 * skew between server and client). Capped at 24 h - anything older
 * means the vessel should already have been pruned.
 */
function formatAgo(ageSec) {
    if (typeof ageSec !== 'number' || !isFinite(ageSec) || ageSec < 0) return null;
    if (ageSec < 60) return `${Math.round(ageSec)} s ago`;
    const m = Math.floor(ageSec / 60);
    if (m < 60) return `${m} min ago`;
    const h = Math.floor(m / 60);
    const rem = m - h * 60;
    return `${h} h ${rem} min ago`;
}

/**
 * Builds the full AIS popup HTML string from a vessel snapshot.
 * Called lazily - only when the popup is actually about to open or
 * is already open and the data changed. Building 200+ of these
 * every 3 s when the user isn't looking at any of them was visible
 * perf overhead on a weak client.
 */
function buildAisPopupHtml(snap) {
    const { v, selfLat: _selfLat, selfLon: _selfLon, cpaInfo, isDangerEff, isWarning } = snap;
    const mmsi = v.mmsi || '';
    const callsign = v.callsign ? esc(v.callsign) : '';
    const sog = v.sogMs != null ? (v.sogMs * MS_TO_KNOTS).toFixed(1) : '--';
    const cogDeg = v.cogRad != null ? (v.cogRad * DEG).toFixed(0) : '--';
    const hdgDeg = v.headingRad != null ? (v.headingRad * DEG).toFixed(0) : '--';
    const type = v.shipType ? esc(v.shipType) : '';
    const dist = haversineMeters(_selfLat, _selfLon, v.lat, v.lon) * NM_PER_METER;
    const brg = bearingDeg(_selfLat, _selfLon, v.lat, v.lon);
    // Last-AIS-evidence row: "13:42 (3 min ago)". Formats LOCAL 24h
    // hh:mm so the helm reads it against the chartplotter clock, with
    // an "ago" tail computed live in the browser (paused-tab clocks
    // don't drift relative to v.ageSec since C# stamps both fields off
    // the same UTC instant). When the wire field is missing we fall
    // back to the C#-computed ageSec alone - the helm still gets the
    // age, just not a wall-clock anchor.
    const lastAisHtml = formatLastAisSeenRow(v.lastAisSeen, v.ageSec);

    // Title resolution lives in resolveAisPopupTitle so the precedence
    // is testable in isolation (Node, no DOM). The resolver returns a
    // raw string; we esc() at the call site since the popup template
    // is HTML.
    const cachedName = (!v.name && mmsi) ? vesselNameCacheGet(mmsi) : undefined;
    const displayTitle = esc(resolveAisPopupTitle({
        displayName: v.displayName,
        name: v.name,
        callsign: v.callsign,
        mmsi,
        buddy: !!v.buddy,
        cachedName,
    }));

    // Compact identity subtitle (helm-feedback round 2: "MMSI 538071935 ·
    // Call V7A6238 · Sailing" reads as one quick line under the title;
    // previously each was a row in the data table eating vertical space).
    // Round 3 adds LOA / beam (AIS Type 5 / 24 static, often absent on
    // class-B targets that don't broadcast static - which is why we
    // append to the parts list rather than reserving a column: rows
    // without dimensions don't grow the popup at all).
    const subtitleParts = [];
    if (mmsi) subtitleParts.push(`MMSI ${esc(mmsi)}`);
    if (callsign) subtitleParts.push(`Call ${callsign}`);
    if (type) subtitleParts.push(type);
    // Dimensions: "12.5 × 4.2 m" when both present, "L 12.5 m" or
    // "B 4.2 m" when only one. Skip entirely when both null -
    // helm-feedback was explicit: "show nothing if not present".
    const loa = (typeof v.loaM === 'number' && isFinite(v.loaM)) ? v.loaM : null;
    const beam = (typeof v.beamM === 'number' && isFinite(v.beamM)) ? v.beamM : null;
    if (loa != null && beam != null) {
        subtitleParts.push(`${loa.toFixed(1)} &times; ${beam.toFixed(1)} m`);
    } else if (loa != null) {
        subtitleParts.push(`L ${loa.toFixed(1)} m`);
    } else if (beam != null) {
        subtitleParts.push(`B ${beam.toFixed(1)} m`);
    }
    const subtitleHtml = subtitleParts.length > 0
        ? `<div class="ais-popup-subtitle">${subtitleParts.join(' &middot; ')}</div>`
        : '';

    // CPA: same font size as the rest of the table; just bold the
    // value when within the danger envelope. Previous version put the
    // CPA in a larger / more padded row which dominated the popup
    // height on touch devices to the point of unusability (helm
    // screenshot showed the popup taller than a phone screen).
    let cpaHtml = '';
    if (cpaInfo && cpaInfo.tcpa > 0) {
        const cls = isDangerEff ? 'ais-popup-cpa-danger' : 'ais-popup-cpa';
        // Bold the NUMBERS only - "nm" / "in" / "min" are scaffolding
        // and the eye should latch on the magnitudes. Per-token <strong>
        // wrapping; the surrounding cell drops its global font-weight
        // override (see .ais-popup-cpa rule in app.css) so the units
        // sit at regular weight beside the bold values.
        cpaHtml = `<tr><td>CPA</td><td class="${cls}">` +
            `<strong>${cpaInfo.cpa.toFixed(2)}</strong>nm in ` +
            `<strong>${cpaInfo.tcpa.toFixed(0)}</strong>min` +
            `</td></tr>`;
    }

    // COLREGS rows: label on the LEFT (like every other data row),
    // role + classification stacked in the value cell on the RIGHT.
    // Role first (bold + coloured - it's the action the helm has to
    // take), classification under it (regular weight). The "?" opens
    // an in-app modal via the data-ona-colregs hook (the previous
    // /help/colregs link was broken under the SK plugin mount and
    // navigated away from the map besides). Two rows so the role
    // chip + classification both have room to breathe at any popup
    // width without wrapping mid-phrase.
    let colregsHtml = '';
    if (v.colregsLabel) {
        const roleHtml = v.colregsRole
            ? `<div class="ais-popup-colregs-role ${v.colregsRole === 'Give way' ? 'ais-popup-colregs-give-way' : 'ais-popup-colregs-stand-on'}">${esc(v.colregsRole)}</div>`
            : '';
        const labelHtml = `<div class="ais-popup-colregs-label">${esc(v.colregsLabel)}</div>`;
        // Label cell carries an extra class so we can override the
        // shared 56 px first-column width: "COLREGS ?" is wider than
        // 56 px, so without nowrap the "?" wrapped onto its own row
        // BELOW the label and read as orphaned punctuation.
        colregsHtml = `<tr>` +
            `<td class="ais-popup-colregs-label-cell">` +
                `COLREGS ` +
                `<a href="#" data-ona-colregs="1" class="ais-popup-colregs-help" ` +
                    `title="Open COLREGS quick reference">?</a>` +
            `</td>` +
            `<td class="ais-popup-colregs-cell">${roleHtml}${labelHtml}</td>` +
            `</tr>`;
    }

    // External lookup links (free, no API key needed). VesselFinder's
    // search page uses ?name= even for MMSI queries.
    const mtUrl = mmsi ? `https://www.marinetraffic.com/en/ais/details/ships/mmsi:${esc(mmsi)}` : '';
    const vfUrl = mmsi ? `https://www.vesselfinder.com/vessels?name=${esc(mmsi)}` : '';

    // Buddy toggle moved into the title row (right-aligned, regular
    // weight) per helm-feedback: the popup header had wasted right-
    // side whitespace and the buddy action belonged with the vessel
    // identity, not below it. data-ona-buddy is the same attribute
    // the existing delegated handler on mapEl already listens for;
    // no new wiring needed.
    const buddyLabel = v.buddy ? '★ Remove buddy' : '☆ Add buddy';
    const buddyAttrs = `data-ona-buddy="1" data-ctx="${esc(v.context)}" data-mmsi="${esc(mmsi || '')}"`
        + ` data-nm="${esc(v.name || '')}" data-is="${v.buddy ? '1' : '0'}"`;
    const buddyHeaderHtml = mmsi
        ? `<a href="#" ${buddyAttrs} class="ais-popup-buddy">${buddyLabel}</a>`
        : '';
    const showSnooze = !v.buddy && (isDangerEff || isWarning);
    const snoozeAttrs = `data-ona-snooze="1" data-ctx="${esc(v.context)}" data-nm="${esc(v.name || mmsi || '')}"`;
    const snoozeHtml = showSnooze
        ? `<a href="#" ${snoozeAttrs} class="ais-popup-snooze">♫ Snooze alarm</a>`
        : '';

    // Footer holds the external lookups + (only when relevant) the
    // snooze action. Buddy moved up to the title row; the footer
    // stays for the wider "look this vessel up elsewhere" intent
    // and the situational snooze link. Hidden entirely when there's
    // nothing to show (no MMSI + not snoozeable).
    let linksHtml = '';
    if (mmsi || showSnooze) {
        const rows = [];
        if (mmsi) {
            rows.push(
                `<div class="ais-popup-links-row">` +
                `<a href="${mtUrl}" target="_blank" rel="noopener" class="ais-popup-extern">MarineTraffic</a>` +
                `<a href="${vfUrl}" target="_blank" rel="noopener" class="ais-popup-extern">VesselFinder</a>` +
                `</div>`
            );
        }
        if (snoozeHtml) {
            rows.push(`<div class="ais-popup-links-row">${snoozeHtml}</div>`);
        }
        linksHtml = `<div class="ais-popup-footer">` + rows.join('') + `</div>`;
    }

    // Country flag from signalk-flags plugin. flagImgHtml caches the
    // SVG as a data URI on first popup-open; every subsequent open
    // for the same MMSI is served from cache (no network round-trip,
    // no dependence on the plugin's HTTP Cache-Control). 404s on
    // servers without the plugin negative-cache silently.
    const flagHtml = flagImgHtml(mmsi);

    return (
        `<div class="ais-popup-content">` +
        `<div class="ais-popup-header">` +
            `<div class="ais-popup-title">${flagHtml}${displayTitle}</div>` +
            buddyHeaderHtml +
        `</div>` +
        subtitleHtml +
        `<table class="ais-popup-table">` +
          cpaHtml +
          colregsHtml +
          `<tr><td>SOG</td><td>${sog} kn</td></tr>` +
          `<tr><td>COG</td><td>${cogDeg}&deg;</td></tr>` +
          `<tr><td>HDG</td><td>${hdgDeg}&deg;</td></tr>` +
          `<tr><td>Dist</td><td>${dist.toFixed(2)} nm</td></tr>` +
          `<tr><td>BRG</td><td>${brg.toFixed(0)}&deg;</td></tr>` +
          lastAisHtml +
        `</table>` +
        linksHtml +
        `</div>`
    );
}

// --- main update ---

export function updateAisTargets(vessels) {
    if (!mapRef) return;
    // During an active drag a full AIS rebuild is the largest per-frame
    // cost in the module (200+ markers, CPA overlays, trails, popups).
    // Defer until the user lets go; the next 3 s tick picks up any
    // changes that happened during the drag. Safety is preserved
    // because alarms run on the C# side off the delta stream, not
    // off the JS marker state.
    if (isSlowClient && mapRef.dragging && mapRef.dragging._moving) return;
    const seen = new Set();

    for (const v of vessels) {
        seen.add(v.context);
        if (v.lat == null || v.lon == null || !isFinite(v.lat) || !isFinite(v.lon)) continue;

        // CPA + TCPA come pre-computed from the C# side (Utilities/Cpa)
        // so the map marker path and the Layers-panel list can't disagree.
        // Shape the expected record for the rest of the loop.
        const cpaInfo = (v.cpaNm != null && v.tcpaMin != null)
            ? { cpa: v.cpaNm, tcpa: v.tcpaMin }
            : null;
        // CPA threat band is computed C#-side (Cpa.ClassifyThreat)
        // using the helm's two-tier thresholds (alarm + awareness
        // distance + time). Three buckets: "alarm" (red marker + red
        // crossing line + audible klaxon), "awareness" (amber chart
        // chip, silent), "none" (no overlay). Buddies are exempted
        // on the C# side so we don't re-check here.
        const isDangerEff = v.cpaThreat === 'alarm';
        const isWarning   = v.cpaThreat === 'awareness';
        // Visual fields are resolved on the C# side (AisPalette /
        // AisSart) and arrive on the vessel payload:
        //   v.sartCategory  - "SART"/"MOB"/"EPIRB" or null
        //   v.glyphCategory - "sail"/"fish"/"commercial"/"service" or null
        //   v.shipColor     - hex string from the ship-type palette
        // JS only applies the runtime overrides (danger / buddy) since
        // those are derived from CPA state that's computed here in JS.
        const isSart = v.sartCategory != null;

        // Radar targets use their own outline-triangle icon in a fixed tan
        // tone; AIS targets fall back to the C#-resolved ship-type colour.
        const isRadar = v.source === 'radar';
        let color;
        if (isRadar) {
            color = isDangerEff ? colors.danger : colors.radar;
        } else if (v.buddy) {
            color = colors.buddy;              // buddies always win
        } else if (isDangerEff) {
            color = colors.danger;             // CPA alarm active
        } else {
            color = v.shipColor || '#e0c9a6';     // palette default from C#
        }
        const category = isRadar ? null : v.glyphCategory;
        const icon = isSart
            ? getSartIcon(v.sartCategory)
            : (isRadar ? getRadarIcon(color) : getAisIcon(color, category));

        let marker = aisMarkers.get(v.context);
        // Scratch tuple reused across vessels for setLatLng. L.marker's
        // ctor copies the values into its own LatLng so passing the
        // same array each call is safe; setLatLng accepts the array
        // form without holding the ref. Eliminates ~200 fresh
        // [lat, lon] allocations per push on a busy harbour.
        _scratchLatLng[0] = v.lat;
        _scratchLatLng[1] = v.lon;
        if (!marker) {
            marker = L.marker(_scratchLatLng, { icon }).addTo(mapRef);
            // Stash the icon ref on the marker so the per-tick path
            // below can skip setIcon when nothing changed - the icon
            // caches return the SAME divIcon reference for the same
            // (color, category, source) tuple, so a strict-equality
            // compare detects "no rebuild needed". setIcon detaches
            // and re-attaches the marker DOM element on every call,
            // which on a 200-vessel tick is ~5-15 ms wasted in the
            // marker-pane reflow. The icon is owned by Leaflet
            // afterwards but the cached ref is what we set, so the
            // identity check is safe.
            marker._lastIcon = icon;
            aisMarkers.set(v.context, marker);
            // During route / polygon / measurement edit, a tap on a vessel
            // should behave like a tap on empty water: append a waypoint,
            // not open the vessel popup. Without this guard the click
            // reaches the marker first (Leaflet's default binding), the
            // popup shows, and the route never picks up the point. We
            // attach the guard on FIRST CREATE so the once-per-marker
            // cost is trivial even in 200-vessel harbours.
            marker.on('click', (ev) => {
                const flags = getEditModeFlags();
                if (flags.routeEdit || flags.polygonEdit || flags.measure) {
                    L.DomEvent.stopPropagation(ev);
                    L.DomEvent.preventDefault(ev);
                    const ll = ev.latlng || marker.getLatLng();
                    // A click ON a vessel marker is unambiguous: the
                    // helm tapped a specific AIS target. Edit-mode
                    // dispatch follows the historical priority (route
                    // -> polygon -> measure); the measure-first
                    // override only applies to empty-map clicks (see
                    // leafletInterop.js::map.on('click')).
                    if (flags.routeEdit)         editModeAddPoint('route', ll.lat, ll.lng);
                    else if (flags.polygonEdit)  editModeAddPoint('polygon', ll.lat, ll.lng);
                    else                         editModeAddPoint('measure', ll.lat, ll.lng);
                    marker.closePopup();
                    return;
                }
                // Outside edit modes: toggle the closing-encounter
                // overlay selection for awareness-tier targets. Alarm-
                // tier targets always show the overlay (rendered by
                // updateAisTargets), so click here only matters for
                // awareness. Leaflet's default popup-open still fires
                // alongside; this handler runs first.
                toggleAwarenessOverlay(marker._onaVesselSnapshot);
            });
        } else {
            // Skip setLatLng when the position is unchanged. Leaflet's
            // setLatLng triggers a project + DOM transform write even
            // when the LatLng values match; on a 200-vessel harbour at
            // 3 Hz that's hundreds of redundant transform writes per
            // second. The pos-equality cache lives on the marker so a
            // setIcon (which rebuilds the DOM but not the LatLng) keeps
            // the cache valid.
            if (marker._lastLat !== v.lat || marker._lastLon !== v.lon) {
                marker.setLatLng(_scratchLatLng);
                marker._lastLat = v.lat;
                marker._lastLon = v.lon;
            }
            // Identity check against the cached ref: the icon caches
            // (aisIconCache / radarIconCache / sartIconCache) return
            // the same divIcon for the same input tuple, so a strict-
            // equality compare detects "nothing to rebuild". setIcon
            // is a DOM detach/reattach + classList re-apply that on
            // 200 vessels at 0.33 Hz (the post-version-skip cadence)
            // would still be ~66 DOM rebuilds/s of work that doesn't
            // change pixels.
            if (marker._lastIcon !== icon) {
                marker.setIcon(icon);
                marker._lastIcon = icon;
            }
        }
        if (!isSart) rotateMarker(marker, v.cogRad ?? v.headingRad);

        // Hoist getElement() once: the cpa-pulse + staleness-opacity
        // blocks below both need the marker DOM element. The previous
        // shape called getElement() twice per vessel per tick; on a
        // 200-vessel harbour at 3 Hz that's 1200 redundant lookups/s
        // (Leaflet's getElement walks the layer's renderer to fish
        // out the icon's <div>; cheap individually, expensive in a
        // tight loop).
        const el = marker.getElement();

        // Pulse an expanding red ring around any AIS / radar target
        // whose CPA is in the "danger" band (matches the colors.danger
        // tint on the chevron). Adds a .cpa-pulse class to the marker
        // element, which the CSS drives via ::after. SART gets its own
        // pulse so we skip it here to avoid double-pulsing.
        // Short-circuit when the desired state matches what we last
        // wrote - classList.toggle is NOT a no-op when the state
        // already matches; V8 invalidates the element's classList
        // cache on every call. With 200 vessels at 0.33 Hz that's
        // ~66 wasted DOM mutations per second on the steady state
        // where no vessel's CPA bucket changed.
        const wantPulse = !isSart && isDangerEff;
        if (el && marker._lastPulse !== wantPulse) {
            el.classList.toggle('cpa-pulse', wantPulse);
            marker._lastPulse = wantPulse;
        }

        // Vessel staleness. Anything not heard from in >30 s is
        // geometrically stale - its rendered position is a guess,
        // not a fix. Fade the marker + trail so the helm's eye lands
        // on live targets first. SART pulses regardless (life-safety
        // beacons can drop out briefly and still matter); buddies
        // also keep full opacity because the "where's my friend"
        // workflow tolerates lateness. When a previously-faded target
        // flips to SART/buddy status mid-session we MUST clear the
        // opacity style we wrote earlier, otherwise it stays dim.
        if (el) {
            if (isSart || v.buddy) {
                if (el.style.opacity !== '') el.style.opacity = '';
            } else {
                // The fade ramp lives in C# (StalenessOpacity.Compute);
                // format.js mirrors it. Null means "fresh, clear inline
                // opacity so the CSS default applies". The faded-floor
                // boundary follows the helm-configurable AIS inactive
                // threshold so a harbour helm can dim ghosts sooner
                // than the default 5 min.
                const ageSec = v.ageSec ?? 0;
                const op = stalenessOpacity(ageSec, aisInactiveSeconds) ?? '';
                // Only write when the bucket actually changes; 200+
                // vessels in a harbour re-writing style every tick
                // invalidates layout for nothing.
                if (el.style.opacity !== op) el.style.opacity = op;
            }
        }

        // Name label visible at zoom >= 12. Resolution (name -> mmsi,
        // with buddy star prefix) happens C#-side - Map.razor.PushAisTargets
        // stamps v.displayName so this label and any other label-rendering
        // surface share one fallback chain. Suppressed in harbor mode
        // to keep the chart legible when entering a busy port AND when
        // the target is past the helm-configured "AIS inactive"
        // threshold so a chart full of ghost MMSIs doesn't drown out
        // the live targets the helm actually has to react to. SART /
        // buddy keep their labels regardless of fade (life-safety and
        // "where's my friend" both tolerate lateness).
        const displayName = v.displayName || null;
        const ageSecLbl = v.ageSec ?? 0;
        const labelStaleHide = ageSecLbl >= aisInactiveSeconds && !isSart && !v.buddy;
        if (displayName && !harborMode && aisLabelsVisible && !labelStaleHide) {
            if (!aisLabels[v.context]) {
                aisLabels[v.context] = L.tooltip({
                    permanent: true, direction: 'right', offset: [12, 0],
                    className: 'ais-label'
                });
                marker.bindTooltip(aisLabels[v.context]);
            }
            // Cache last-pushed display string per context so identical
            // values don't trigger setContent's DOM mutation. esc(name)
            // is a fresh string each call but the resolved value is
            // mostly stable (changes only on rename / buddy-star flip).
            const escapedName = esc(displayName);
            const tip = aisLabels[v.context];
            if (tip._lastContent !== escapedName) {
                tip.setContent(escapedName);
                tip._lastContent = escapedName;
            }
        } else if (aisLabels[v.context]) {
            // Target just crossed into the stale band (or harbor mode
            // / labels-off flipped). Tear down the tooltip so the helm
            // sees the change next paint; the create-gate above rebuilds
            // it from v.displayName once the target goes fresh again.
            try { marker.unbindTooltip(); } catch (_) { /* marker gone */ }
            delete aisLabels[v.context];
        }

        // Rich popup with vessel details and external lookup links.
        // Building the HTML for every vessel every tick (200+ in a busy
        // harbour, 3 s cadence) shows up in profiles as measurable
        // overhead even though most popups are never opened. We now
        // STASH the snapshot on the marker and only rebuild when the
        // popup is actually visible - once on popupopen and again on
        // each tick the popup stays open. buildAisPopupHtml reads the
        // stashed data directly so the per-vessel HTML work is deferred
        // to the lazy path.
        marker._onaVesselSnapshot = {
            v, selfLat, selfLon, cpaInfo,
            isDangerEff, isWarning,
        };

        if (!marker.getPopup()) {
            // First bind: placeholder content + popupopen listener that
            // rebuilds the real HTML from the stashed snapshot before
            // showing.
            // maxWidth 420 (was 280): the popup contains ~8 label/value
            // rows plus an action row with MarineTraffic / VesselFinder /
            // Buddy / Snooze links. At 280 px the action row wrapped onto
            // three lines on iPad landscape and the MT / VF links split
            // across rows; 420 keeps them on one line and reads cleaner.
            // autoPan re-enabled (was false earlier under "no focus
            // change on collision course"): in the field a marker near
            // the top of the screen meant the popup rendered above it
            // and went off-viewport entirely - the helm couldn't
            // read the CPA / COG row at all. autoPan with a generous
            // padding still moves the map only when strictly needed
            // and keeps own-boat in view at all but the most extreme
            // edge cases. keepInView pins the popup if the helm then
            // pans manually so it doesn't slide off again.
            marker.bindPopup('', {
                closeButton: false,
                maxWidth: 420,
                className: 'ais-popup',
                autoPan: true,
                autoPanPadding: [20, 20],
                keepInView: true,
            });
            // Adjust popup placement BEFORE Leaflet's click handler
            // calls openPopup. `preclick` is the Leaflet hook that fires
            // ahead of `click` for exactly this kind of override; a plain
            // `click` listener added after bindPopup would fire AFTER
            // openPopup and the new options wouldn't apply until the
            // next click.
            //
            // Follow-mode behaviour: helm wants their boat to stay
            // centred when they tap an AIS target. Leaflet's autoPan
            // would scroll the chart to fit the popup, fighting follow.
            // We disable autoPan and instead push the popup body BELOW
            // the marker (large positive Y offset) when the marker
            // sits in the top portion of the viewport so the popup
            // stays on screen without moving the map. Out of follow
            // mode the original autoPan behaviour is unchanged.
            marker.on('preclick', () => {
                const popup = marker.getPopup();
                if (!popup) return;
                if (!followBoat || !mapRef) {
                    popup.options.autoPan = true;
                    popup.options.offset = L.point(0, 7);
                    return;
                }
                popup.options.autoPan = false;
                // Threshold: top ~30 % of the viewport is the danger
                // zone for a default popup whose body extends UP from
                // the marker. Below that the default placement fits.
                const cp = mapRef.latLngToContainerPoint(marker.getLatLng());
                const size = mapRef.getSize();
                if (cp.y < size.y * 0.3) {
                    // Push the popup tip ~240 px below the marker; the
                    // body still extends UP from the tip, so the body
                    // ends up centred near the marker / vertical
                    // midline instead of off-screen above.
                    popup.options.offset = L.point(0, 240);
                } else {
                    popup.options.offset = L.point(0, 7);
                }
            });
            marker.on('popupopen', () => {
                if (marker._onaVesselSnapshot) {
                    marker.setPopupContent(buildAisPopupHtml(marker._onaVesselSnapshot));
                }
            });
        } else if (marker.isPopupOpen()) {
            // Popup is on screen right now - user is watching. Refresh
            // live so the SOG / CPA / buddy toggle label update without
            // a close-reopen round-trip.
            marker.setPopupContent(buildAisPopupHtml(marker._onaVesselSnapshot));
        }

        // External name lookup kick-off stays on the fast path: the
        // result affects the on-chart label tooltip (always visible at
        // zoom >= 12), not just the popup, so we don't want to gate it
        // on popup-open.
        if (!v.name && v.mmsi && !vesselNameCacheHas(v.mmsi)) {
            resolveVesselName(v.context, v.mmsi);
        }

        // Trail: rendered as a slate dashed polyline of recent
        // positions. The sliding-window state lives in C#
        // (AisTrailBuffer); the payload carries fresh coords on
        // v.trail only when the buffer changed since the last push.
        // Absent v.trail = "leave the existing polyline alone".
        if (v.trail !== undefined) updateAisTrail(v.context, v.trail);

        // Course vector. Drawn for any vessel with a known COG and a
        // non-trivial SOG (vectorEnd returns null below the 0.1 m/s
        // floor). Harbor mode no longer hides this - the moored-
        // vessel filter on the C# side (HarborAisFilter) already
        // drops every dwelling target before it reaches us, so the
        // vessels still on screen in harbor mode are the ones
        // actually moving and the helm needs to see where they're
        // headed. The earlier "every vector sweeps across every
        // marker" decluttering was working off the un-filtered list.
        //
        // Vector colour is the vessel's NATURAL palette tone, NOT
        // the danger-red `color` variable used for the marker icon.
        // Helm-feedback: when CPA fires, the vessel triangle should
        // turn red (that's the alarm signal), but the COG vector is
        // a "this is where it's heading" line and shouldn't lose
        // its identity colour just because the alarm is on. The
        // vector continues to point the same direction with or
        // without the alarm; the alarm is the marker's job.
        const vecColor = isRadar ? colors.radar
            : (v.buddy ? colors.buddy : (v.shipColor || '#e0c9a6'));
        // Vector endpoint precomputed in C# (AisPushService.FillPayload
        // -> GeoMath.VectorEnd) using the helm-configured
        // AisCogVectorMinutes. Null means below the 0.1 m/s stationary
        // threshold or COG / SOG missing - same semantics as the
        // previous JS-side vectorEnd() returning null. Eliminates 200+
        // destPoint() trig calls per AIS push from the JS hot path.
        const end = (v.vectorEndLat != null && v.vectorEndLon != null)
            ? [v.vectorEndLat, v.vectorEndLon]
            : null;
        if (end) {
            let vec = aisVectors[v.context];
            if (!vec) {
                // Custom pane (zIndex 410) so the vector renders
                // ABOVE the CPA crossing-situation lines (overlayPane,
                // zIndex 400). Helm-feedback: the long red CPA line
                // was previously obscuring the smaller COG vectors
                // for vessels not in the alarm.
                vec = L.polyline([[v.lat, v.lon], end], {
                    color: vecColor, weight: 1.5, dashArray: '6,4',
                    pane: 'aisCogVectors',
                }).addTo(mapRef);
                aisVectors[v.context] = vec;
            } else {
                vec.setLatLngs([[v.lat, v.lon], end]);
                vec.setStyle({ color: vecColor });
            }
            // Small circle at the tip of the vector - "boat is here
            // at +VECTOR_MINUTES" landmark so the helm reads the
            // endpoint without extrapolating from the trailing
            // dashes. Same colour as the vector so the eye groups
            // them; non-interactive so it doesn't intercept clicks
            // that should hit the marker triangle. Same pane as the
            // vector so they stack above CPA lines together.
            let tip = aisVectorTips[v.context];
            if (!tip) {
                tip = L.circleMarker(end, {
                    radius: 2.5,
                    color: vecColor,
                    fillColor: vecColor,
                    fillOpacity: 1,
                    weight: 1,
                    interactive: false,
                    pane: 'aisCogVectors',
                }).addTo(mapRef);
                aisVectorTips[v.context] = tip;
            } else {
                tip.setLatLng(end);
                tip.setStyle({ color: vecColor, fillColor: vecColor });
            }
        } else if (aisVectorTips[v.context]) {
            // Vessel went stationary (vectorEnd returned null): drop
            // the tip alongside the vector.
            mapRef.removeLayer(aisVectorTips[v.context]);
            delete aisVectorTips[v.context];
        }

        // Crossing-situation overlay. Five elements painted together:
        //   * own dashed line from own boat to own's CPA point
        //   * own X marker at own's CPA point
        //   * target dashed line from target to target's CPA point
        //   * target X marker at target's CPA point
        //   * segment line between the two X markers (the actual CPA
        //     distance) with the name/CPA/TCPA label at its midpoint
        // Rendered for alarm-tier targets always; for awareness-tier
        // targets only when the helm has click-selected them (toggle).
        // Buddies never render the overlay - they're exempt from the
        // alarm pipeline and the red lines would be misleading. The
        // overlay stays on in harbor mode (the moored-vessel filter on
        // the C# side drops harmless dwellers); only the AUDIO alarm
        // is suppressed in harbor.
        const shouldShowOverlay = isDangerEff
            || (isWarning && v.context === _selectedAwarenessCtx);
        if (shouldShowOverlay && cpaInfo && cpaInfo.tcpa > 0
            && v.cpaPointLat != null && v.cpaPointLon != null) {
            const severity = isDangerEff ? 'danger' : 'warn';
            const lineColor = isDangerEff ? colors.mob : colors.guardWarn;
            const cpaName = v.displayName || v.name || v.mmsi || 'Unknown';
            const labelText = `<strong>${esc(cpaName)}</strong><br>${cpaInfo.cpa.toFixed(2)}nm  T -${cpaInfo.tcpa.toFixed(0)}′`;
            renderFullCpaOverlay(
                v.context, [v.lat, v.lon],
                [v.cpaPointLat, v.cpaPointLon],
                (v.ownCpaPointLat != null && v.ownCpaPointLon != null)
                    ? [v.ownCpaPointLat, v.ownCpaPointLon] : null,
                lineColor, severity, labelText);
            aisCpaLastSeverity[v.context] = severity;
        } else {
            removeCpaOverlay(v.context);
        }
    }

    // Remove stale markers. Iterating the Map directly avoids the
    // Object.keys() snapshot a plain-object dict would have required;
    // Map iteration is safe under in-flight delete in V8 (the spec
    // guarantees iteration sees entries that exist at iterator-step
    // time, and our delete happens via Map.delete which is the
    // approved mutation path).
    for (const ctx of aisMarkers.keys()) {
        if (!seen.has(ctx)) {
            mapRef.removeLayer(aisMarkers.get(ctx));
            aisMarkers.delete(ctx);
            if (aisVectors[ctx]) { mapRef.removeLayer(aisVectors[ctx]); delete aisVectors[ctx]; }
            if (aisVectorTips[ctx]) { mapRef.removeLayer(aisVectorTips[ctx]); delete aisVectorTips[ctx]; }
            delete aisLabels[ctx];
            removeCpaOverlay(ctx);
            removeAisTrail(ctx);
        }
    }
}

// Apply a fresh trail to the per-vessel polyline. Trail data is owned
// by AisTrailBuffer on the C# side; this function is invoked only when
// the payload carries a non-undefined `v.trail` (the C# side gates on
// AisTrailBuffer.ConsumeDirty so unchanged trails skip the wire). A
// null `coords` (or fewer than 4 numbers = < 2 points) means "trail
// dropped below the 2-point minimum / aged out" - remove the existing
// polyline; otherwise unpack the flat [lat,lon,lat,lon,...] array into
// Leaflet LatLng tuples and update or create the polyline.
function updateAisTrail(ctx, coords) {
    let line = aisTrailLines[ctx];
    if (!coords || coords.length < 4) {
        if (line) { mapRef.removeLayer(line); delete aisTrailLines[ctx]; }
        return;
    }
    const pairs = unpackLatLonPairs(coords);
    if (!line) {
        // Dashed slate line: distinguishes the historical trail from
        // the SOLID forward COG vector that points where the vessel
        // is GOING. With both rendered solid the helm couldn't tell
        // forward from backward at a glance.
        line = L.polyline(pairs, {
            color: '#94a3b8', weight: 1.2, opacity: 0.5,
            dashArray: '2,4', interactive: false,
        }).addTo(mapRef);
        aisTrailLines[ctx] = line;
    } else {
        line.setLatLngs(pairs);
    }
}

function removeAisTrail(ctx) {
    if (aisTrailLines[ctx]) { mapRef.removeLayer(aisTrailLines[ctx]); delete aisTrailLines[ctx]; }
}

function updateCpaLine(store, ctx, from, to, color) {
    let line = store[ctx];
    if (!line) {
        // Faint thin dashed style: a 9-minute TCPA stretches HALF a
        // nautical mile of line across the chart, so anything bolder
        // would dominate the visual and pull the eye off the X
        // marker at the end. weight 0.8 + dash 2,7 + opacity 0.5
        // reads as a trace of the geometry; the X and its label
        // carry the emphasis.
        line = L.polyline([from, to], {
            color, weight: 0.8, dashArray: '2,7', opacity: 0.5
        }).addTo(mapRef);
        store[ctx] = line;
    } else {
        line.setLatLngs([from, to]);
        line.setStyle({ color });
    }
}

/// Build / update the "×" marker at a closest-approach endpoint.
/// `withTooltip` true on the target side carries the label; the
/// own side renders just a small cross since the label is bound
/// to the target marker. Severity controls the colour class.
/// Click on either X opens the target vessel's popup so a helm
/// can drill into name / MMSI / COLREGS without finding the
/// triangle marker first.
function updateCpaXMarker(store, ctx, latlon, severity, withTooltip, labelText, vesselCtx) {
    let m = store[ctx];
    const className = `cpa-x-marker cpa-x-${severity}`;
    if (!m) {
        const icon = L.divIcon({
            className,
            html: '<div class="cpa-x">×</div>',
            iconSize: [18, 18],
            iconAnchor: [9, 9],
        });
        m = L.marker(latlon, {
            icon,
            interactive: true,
            // keepInView=false so the marker doesn't drag the map
            // pan when the boat moves toward it.
            keyboard: false,
        }).addTo(mapRef);
        store[ctx] = m;
        if (vesselCtx) attachCpaXClick(m, vesselCtx);
        if (withTooltip && labelText) {
            const isDanger = severity === 'danger';
            // permanent for danger so the alarm chip is always
            // visible; non-permanent for warn so it auto-hides
            // and re-opens on hover. direction: 'top' puts the
            // chip above the X rather than over it.
            m.bindTooltip(labelText, {
                permanent: isDanger,
                direction: 'top',
                offset: [0, -4],
                className: `cpa-label cpa-${severity}`,
            });
        }
    } else {
        m.setLatLng(latlon);
        const el = m.getElement();
        if (el) {
            el.classList.remove('cpa-x-danger', 'cpa-x-warn');
            el.classList.add(`cpa-x-${severity}`);
        }
        if (withTooltip && labelText) {
            const tt = m.getTooltip();
            if (tt) {
                tt.setContent(labelText);
                const ttEl = tt.getElement();
                if (ttEl) {
                    ttEl.classList.remove('cpa-danger', 'cpa-warn');
                    ttEl.classList.add(`cpa-${severity}`);
                }
                // Severity escalated warn -> danger: flip the
                // tooltip to permanent so it stays visible without
                // requiring hover. Leaflet's tooltip options are
                // settable via `options`; calling openTooltip
                // after the toggle ensures it's open even if the
                // helm wasn't hovering.
                const isDanger = severity === 'danger';
                if (tt.options.permanent !== isDanger) {
                    tt.options.permanent = isDanger;
                    if (isDanger) m.openTooltip();
                }
            }
        }
    }
}

/**
 * Helm click on an AIS marker. Alarm-tier targets always render
 * their overlay (updateAisTargets does it on every push); awareness-
 * tier targets render only when the helm has selected them. Click
 * toggles: a different awareness target replaces the current
 * selection, the same target a second time clears it.
 *
 * Triggers an immediate render so the helm sees the response without
 * waiting for the next ~333 ms AIS push tick. The next push will
 * re-evaluate the gate and keep the rendering consistent.
 */
function toggleAwarenessOverlay(snap) {
    if (!snap || !snap.v) return;
    const v = snap.v;
    if (v.cpaThreat !== 'awareness') {
        // Clicking a non-awareness vessel doesn't move the selection -
        // the helm might be cycling popups without intending to drop
        // the currently selected awareness overlay. Selection only
        // moves on awareness-vessel clicks.
        return;
    }
    const ctx = v.context;
    if (_selectedAwarenessCtx === ctx) {
        // Toggle off.
        _selectedAwarenessCtx = null;
        removeCpaOverlay(ctx);
        return;
    }
    const prev = _selectedAwarenessCtx;
    _selectedAwarenessCtx = ctx;
    if (prev) removeCpaOverlay(prev);
    // Immediate render using the cached snapshot. Same five-element
    // overlay updateAisTargets would paint on the next tick.
    if (snap.cpaInfo && snap.cpaInfo.tcpa > 0
        && v.cpaPointLat != null && v.cpaPointLon != null) {
        const cpaName = v.displayName || v.name || v.mmsi || 'Unknown';
        const labelText = `<strong>${esc(cpaName)}</strong><br>${snap.cpaInfo.cpa.toFixed(2)}nm  T -${snap.cpaInfo.tcpa.toFixed(0)}′`;
        const ownCpa = (v.ownCpaPointLat != null && v.ownCpaPointLon != null)
            ? [v.ownCpaPointLat, v.ownCpaPointLon] : null;
        renderFullCpaOverlay(
            ctx, [v.lat, v.lon],
            [v.cpaPointLat, v.cpaPointLon], ownCpa,
            colors.guardWarn, 'warn', labelText);
        aisCpaLastSeverity[ctx] = 'warn';
    }
}

function removeCpaOverlay(ctx) {
    if (aisCpaOwnLines[ctx]) { mapRef.removeLayer(aisCpaOwnLines[ctx]); delete aisCpaOwnLines[ctx]; }
    if (aisCpaOwnX[ctx])     { mapRef.removeLayer(aisCpaOwnX[ctx]);     delete aisCpaOwnX[ctx]; }
    if (aisCpaTgtLines[ctx]) { mapRef.removeLayer(aisCpaTgtLines[ctx]); delete aisCpaTgtLines[ctx]; }
    if (aisCpaTgtX[ctx])     { mapRef.removeLayer(aisCpaTgtX[ctx]);     delete aisCpaTgtX[ctx]; }
    if (aisCpaSegments[ctx]) { mapRef.removeLayer(aisCpaSegments[ctx]); delete aisCpaSegments[ctx]; }
    delete aisCpaLastSeverity[ctx];
}

/**
 * Render the full closing-encounter overlay for one target:
 *   * dashed line from own boat to own's CPA point
 *   * X marker at own's CPA point (no tooltip - the segment carries it)
 *   * dashed line from target to target's CPA point
 *   * X marker at target's CPA point (no tooltip - the segment carries it)
 *   * solid line between the two X markers (the actual CPA distance)
 *     with the name + CPA + TCPA label at its midpoint
 *
 * `ownCpa` may be null when own snapshot is missing - then only the
 * target-side line + X render (graceful degradation, keeps some
 * signal on the chart even without an own fix). The segment + own
 * line + own X are skipped together since they all depend on the
 * own CPA point.
 */
function renderFullCpaOverlay(ctx, tgtCur, tgtCpa, ownCpa, lineColor, severity, labelText) {
    // Target-side: dashed line target -> target's CPA point, X at CPA.
    // Tooltip moves OFF the X and onto the segment midpoint (see
    // below), so the X here renders without one.
    updateCpaLine(aisCpaTgtLines, ctx, tgtCur, tgtCpa, lineColor);
    updateCpaXMarker(aisCpaTgtX, ctx, tgtCpa, severity, /*withTooltip*/false, null, ctx);
    if (ownCpa == null) {
        // Own snapshot missing - drop the own-side + segment that
        // depend on it. The target-side overlay stays so the helm
        // still sees something even without a fix.
        if (aisCpaOwnLines[ctx]) { mapRef.removeLayer(aisCpaOwnLines[ctx]); delete aisCpaOwnLines[ctx]; }
        if (aisCpaOwnX[ctx])     { mapRef.removeLayer(aisCpaOwnX[ctx]);     delete aisCpaOwnX[ctx]; }
        if (aisCpaSegments[ctx]) { mapRef.removeLayer(aisCpaSegments[ctx]); delete aisCpaSegments[ctx]; }
        return;
    }
    // Own-side: dashed line own boat -> own's CPA point, X at CPA.
    updateCpaLine(aisCpaOwnLines, ctx, [selfLat, selfLon], ownCpa, lineColor);
    updateCpaXMarker(aisCpaOwnX, ctx, ownCpa, severity, /*withTooltip*/false, null, ctx);
    // Segment between the two CPA points. Solid (vs dashed for the
    // projection lines) so the helm reads it as "the closest the two
    // tracks get" rather than as another track projection. Tooltip
    // at midpoint carries the alarm chip - permanent for alarm tier,
    // hover-only for awareness.
    let segment = aisCpaSegments[ctx];
    const segLatLngs = [ownCpa, tgtCpa];
    if (!segment) {
        segment = L.polyline(segLatLngs, {
            color: lineColor, weight: 1.6, opacity: 0.85,
            interactive: true,
        }).addTo(mapRef);
        aisCpaSegments[ctx] = segment;
    } else {
        segment.setLatLngs(segLatLngs);
        segment.setStyle({ color: lineColor });
    }
    const isDanger = severity === 'danger';
    // Place the label ON the segment when there's pixel room
    // (direction: 'center'), or just ABOVE it when the segment is too
    // short for the chip to fit without overlapping the line. The
    // helm-feedback: at zoomed-out scales the two CPA points sit
    // 5-10px apart and the centred chip covered the line + both X
    // markers. Threshold tuned to ~80 px - roughly the chip's own
    // width at the current font size. Re-evaluated on every push so
    // a zoom-in/out lands the label correctly within ~1 tick (~333ms
    // on a fast feed).
    const p1 = mapRef.latLngToLayerPoint(L.latLng(ownCpa[0], ownCpa[1]));
    const p2 = mapRef.latLngToLayerPoint(L.latLng(tgtCpa[0], tgtCpa[1]));
    const segmentPxLen = Math.hypot(p2.x - p1.x, p2.y - p1.y);
    const compactLabel = segmentPxLen < 80;
    const wantDirection = compactLabel ? 'top' : 'center';
    const wantOffset = compactLabel ? [0, -6] : [0, 0];
    const tt = segment.getTooltip();
    if (!tt || tt.options.direction !== wantDirection) {
        // Direction change requires a fresh binding - Leaflet's
        // tooltip options.direction is set at creation; toggling
        // it on a live tooltip leaves the prior anchor in place.
        if (tt) segment.unbindTooltip();
        segment.bindTooltip(labelText, {
            permanent: isDanger,
            direction: wantDirection,
            offset: wantOffset,
            sticky: false,
            className: `cpa-label cpa-${severity}`,
        });
        if (isDanger) segment.openTooltip();
    } else {
        tt.setContent(labelText);
        const ttEl = tt.getElement();
        if (ttEl) {
            ttEl.classList.remove('cpa-danger', 'cpa-warn');
            ttEl.classList.add(`cpa-${severity}`);
        }
        if (tt.options.permanent !== isDanger) {
            tt.options.permanent = isDanger;
            if (isDanger) segment.openTooltip();
        }
    }
    // Clicking the segment behaves the same as clicking the target X:
    // opens the vessel popup so the helm can drill into name / MMSI /
    // COLREGS without finding the triangle marker first.
    if (!segment._onaCpaClickWired) {
        segment.on('click', () => {
            const m = aisMarkers.get(ctx);
            if (m) {
                if (typeof m.openTooltip === 'function') m.openTooltip();
                if (typeof m.openPopup === 'function') m.openPopup();
            }
        });
        segment._onaCpaClickWired = true;
    }
}

/**
 * Wires a click / tap on the target's CPA X marker to open the
 * vessel popup AND show its tooltip. The popup carries full detail
 * (name, MMSI, callsign, SOG, COG, HDG, bearing / distance, COLREGS
 * role, external links, Buddy / Snooze, CPA row); the tooltip is
 * the compact chip the helm reads at a glance. Tapping the X gives
 * the helm both: chip stays open while they pick the next action,
 * popup gives them the deeper drill-in.
 *
 * Hover (mouse only) shows just the tooltip via Leaflet's default
 * tooltip-on-hover behaviour for non-permanent tooltips. Touch is
 * tap-only; iPad helms get the click path.
 */
function attachCpaXClick(marker, vesselContext) {
    marker.on('click', () => {
        marker.openTooltip();
        const target = aisMarkers.get(vesselContext);
        if (target && typeof target.openPopup === 'function') {
            target.openPopup();
        }
    });
}

/** Pans the map to an AIS vessel and opens its popup. Returns true
 *  when a marker existed; false when the context didn't match anything
 *  (vessel aged out, AIS filter hiding it, deleted since the list
 *  rendered). The C# caller uses the return value to toast + restore
 *  follow so the user isn't left wondering why the tap did nothing. */
export function focusVessel(context) {
    const marker = aisMarkers.get(context);
    if (!marker || !mapRef) return false;
    const ll = marker.getLatLng();
    mapRef.panTo(ll, { animate: true });
    marker.openPopup();
    return true;
}

// --- distance rings & harbor mode ---

/**
 * Push helm-configured distance-rings settings from C#. Pure visual
 * scaffolding on own boat - independent of the CPA / TCPA pipeline
 * and of harbor mode. Rings sit at `baseNm × {1..count}`. Defensive
 * coercion on each input: non-finite / non-positive / out-of-range
 * values fall back to the existing module-level value so a corrupted
 * setting can't tear the rings down or paint a 5000-nm ring.
 */
export function setDistanceRings(enabled, baseNm, count) {
    distanceRingsEnabled = !!enabled;
    const b = Number(baseNm);
    if (Number.isFinite(b) && b > 0) distanceRingsBaseNm = b;
    const c = Math.round(Number(count));
    if (Number.isFinite(c) && c >= 1) distanceRingsCount = Math.min(c, 8);
    drawDistanceRings();
}

// Best-effort layer removal that never throws. Some entries in the
// per-context dicts can be null / undefined under tear-down races
// (a concurrent updateAisTargets that just deleted the key, or a
// disposed Leaflet layer); without the guard map.removeLayer(undefined)
// throws TypeError: Cannot read properties of undefined ('_layerAdd')
// and the whole setHarborMode call rejects - which the C# side
// then has to roll back via the toast path. Catching here makes the
// JS-side teardown best-effort and lets the C# happy path stay
// green.
function safeRemoveLayer(layer) {
    if (!layer || !mapRef) return;
    try { mapRef.removeLayer(layer); } catch (_) { /* already gone */ }
}

// Push the helm-configured "AIS inactive" threshold (in minutes) from
// the C# settings. Stored internally as seconds for cheap comparison
// against the per-vessel ageSec we get on the wire. Clamps non-finite
// or non-positive values to the AppSettings default (5 min) so a
// corrupted setting can't disable the stale-rule entirely.
export function setAisInactiveMinutes(minutes) {
    const m = Number(minutes);
    aisInactiveSeconds = (Number.isFinite(m) && m > 0) ? Math.round(m * 60) : 300;
}

// Mirror of leafletInterop.setFollow. Called by the interop ladder
// whenever follow mode flips so the AIS popup-open path can pick a
// direction without autoPan when follow is on (autoPan would scroll
// the map and break follow).
export function setFollow(follow) { followBoat = !!follow; }

// Toggle the persistent AIS-name-label preference. When disabled,
// tears down every existing label so the helm sees the change
// immediately (next updateAisTargets tick won't re-create them
// because the create-gate ANDs aisLabelsVisible). When re-enabled,
// the next tick rebuilds labels from v.displayName as usual.
export function setAisLabelsVisible(enabled) {
    aisLabelsVisible = !!enabled;
    if (!mapRef) return;
    if (!aisLabelsVisible) {
        for (const ctx of Object.keys(aisLabels)) {
            try { aisMarkers.get(ctx)?.unbindTooltip(); } catch (_) { /* marker gone */ }
            delete aisLabels[ctx];
        }
    }
    // Re-enable does nothing on its own: the next AIS push tick sees
    // aisLabelsVisible=true + !harborMode and re-creates each label
    // from v.displayName via the gate at line ~796.
}

export function setHarborMode(enabled) {
    harborMode = !!enabled;
    if (!mapRef) return;
    if (harborMode) {
        // Harbor mode declutter: suppress NAME LABELS (with hundreds of
        // pontoon vessels visible the labels stack into a wall of text)
        // while making way through pontoon traffic. COG vectors and
        // CPA crossing-lines stay visible - the moored-vessel filter
        // on the C# side already drops every dwelling target before it
        // reaches us, so the vessels still on screen are the moving
        // ones the helm needs to track. The next updateAisTargets tick
        // re-evaluates COG / CPA per surviving vessel; nothing to
        // tear down here.
        for (const ctx of Object.keys(aisLabels)) {
            try { aisMarkers.get(ctx)?.unbindTooltip(); } catch (_) { /* marker gone */ }
            delete aisLabels[ctx];
        }
    }
    // Coming out of harbor mode: AIS labels are re-established by
    // the next updateAisTargets tick. Distance rings are not gated
    // by harbor mode so there's nothing to redraw here.
}

// Range-ring label formatter is shared with radarLayer.js via
// format.js (canonical: Format.RangeRingLabel, tested in C#).

// Tear every distance ring + its label off the map and clear the
// parallel arrays. Called when the feature is disabled, when the
// requested count shrinks, and on dispose. safeRemoveLayer swallows
// the "already gone" race so a teardown that crosses a Leaflet
// re-init stays best-effort.
function clearDistanceRings() {
    for (const layer of distanceRings) safeRemoveLayer(layer);
    for (const layer of distanceRingLabels) safeRemoveLayer(layer);
    distanceRings = [];
    distanceRingLabels = [];
    _distanceRingLatDegs = [];
}

function drawDistanceRings() {
    if (!mapRef) return;
    // Disabled -> drop everything. Distance rings are NOT gated by
    // harbor mode: they're a helm-controlled measurement tool, not
    // a collision-related overlay, so harbor's "declutter collision
    // noise" intent doesn't apply. A helm who wants a clean harbor
    // chart toggles distance rings off via Settings.
    if (!distanceRingsEnabled) {
        clearDistanceRings();
        return;
    }
    // Defensive against a corrupted count / base. setDistanceRings
    // already clamps but the module-level state could land here
    // after a hot-reload or a manual console poke - cheap second
    // line of defence.
    const count = Math.max(1, Math.min(8, distanceRingsCount | 0));
    const baseNm = (Number.isFinite(distanceRingsBaseNm) && distanceRingsBaseNm > 0)
        ? distanceRingsBaseNm : 0.5;
    // Shrink: trim trailing rings if the helm reduced the count.
    while (distanceRings.length > count) {
        safeRemoveLayer(distanceRings.pop());
        safeRemoveLayer(distanceRingLabels.pop());
        _distanceRingLatDegs.pop();
    }
    for (let i = 0; i < count; i++) {
        const ringNm = baseNm * (i + 1);
        const radiusM = ringNm * 1852;
        if (i < distanceRings.length) {
            distanceRings[i].setLatLng([selfLat, selfLon]);
            distanceRings[i].setRadius(radiusM);
        } else {
            // Thin grey dashed - reads as measurement scaffolding,
            // not an alarm boundary. Distinct from the amber guard
            // rings so the helm can have both on screen without
            // visual confusion. Non-interactive so the ring never
            // steals pointer events from a vessel marker below.
            distanceRings.push(L.circle([selfLat, selfLon], {
                radius: radiusM,
                color: '#888',
                weight: 1,
                opacity: 0.5,
                dashArray: '2 4',
                fillOpacity: 0,
                interactive: false,
            }).addTo(mapRef));
        }
        // Label at the ring's north edge. Same lat-degree-offset
        // trick as the guard ring labels: divide ONCE here on
        // radius change so per-tick setBoatPosition is division-
        // free.
        const latDeg = radiusM / 111320;
        _distanceRingLatDegs[i] = latDeg;
        const labelLat = selfLat + latDeg;
        const text = rangeRingLabel(ringNm);
        if (i < distanceRingLabels.length) {
            distanceRingLabels[i].setLatLng([labelLat, selfLon]);
            distanceRingLabels[i].setContent(text);
        } else {
            distanceRingLabels.push(L.tooltip({
                permanent: true, direction: 'center', interactive: false,
                className: 'distance-ring-label',
            }).setLatLng([labelLat, selfLon]).setContent(text).addTo(mapRef));
        }
    }
}

export function dispose() {
    // Walk every per-context dict and remove the Leaflet layer from
    // the map BEFORE dropping our reference. The previous shape
    // (`delete aisMarkers[ctx]` only) detached the JS reference but
    // left the layer attached to mapRef's internal _layers map; a
    // route-edit -> re-init cycle leaked every marker, COG vector,
    // CPA line, X-marker, label, and trail polyline that was alive
    // at dispose time. safeRemoveLayer swallows the "already-gone"
    // race so a teardown that crosses a settings flip stays
    // best-effort. Pinned in OnaPlotter.Tests/Js coverage via the
    // dispose smoke test.
    // aisMarkers is a Map; inline the dispose since disposeLayerDict
    // below walks Object.keys() and would treat a Map as empty.
    for (const layer of aisMarkers.values()) safeRemoveLayer(layer);
    aisMarkers.clear();
    disposeLayerDict(aisVectors);
    disposeLayerDict(aisVectorTips);
    disposeLayerDict(aisCpaOwnLines);
    disposeLayerDict(aisCpaOwnX);
    disposeLayerDict(aisCpaTgtLines);
    disposeLayerDict(aisCpaTgtX);
    disposeLayerDict(aisCpaSegments);
    // aisCpaLastSeverity is a string map, not Leaflet layers.
    for (const ctx of Object.keys(aisCpaLastSeverity)) delete aisCpaLastSeverity[ctx];
    _selectedAwarenessCtx = null;
    disposeLayerDict(aisTrailLines);
    disposeLayerDict(aisLabels);
    clearDistanceRings();
    selfLat = 0; selfLon = 0; selfCogRad = null; selfSogMs = null;
    mapRef = null;
    colors = null;
    getDotNetRef = null;
    getEditModeFlags = null;
    editModeAddPoint = null;
    getOwnMmsi = null;
    flagUrl = null;
    rotateMarker = null;
}

// Helper: detach every Leaflet layer in a per-context dict from
// mapRef and clear the entry. Order matters - safeRemoveLayer
// reads mapRef, so we must call it BEFORE dispose() nulls mapRef
// at the end. Each dispose-of-dict pass enumerates a snapshot of
// keys so a removeLayer that triggers an unintended Leaflet event
// can't mutate the dict mid-iteration.
function disposeLayerDict(dict) {
    if (!dict) return;
    for (const ctx of Object.keys(dict)) {
        safeRemoveLayer(dict[ctx]);
        delete dict[ctx];
    }
}
