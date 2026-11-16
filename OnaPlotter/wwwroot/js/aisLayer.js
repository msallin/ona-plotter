// AIS / radar / SART target rendering. Owns:
//   * Per-vessel marker (chevron / radar triangle / SART pulsing
//     bullseye), name label, COG vector + tip, and rolling trail.
//   * CPA crossing-situation overlay: projection lines, X markers at
//     both CPA points and the labelled closest-approach segment.
//   * Distance rings around own boat.
//   * Harbor-mode / labels-off declutter of the name labels.
//
// Every decision (colour role, opacity, label visibility, overlay mode,
// titles, popup numbers, trail deltas) is made C#-side in
// AisLayerController and arrives on the payload; this module applies it.
// Updates are versioned: a full snapshot (baseSeq -1) replaces the
// state, a delta applies on top of the sequence it names. A delta for
// a base this module doesn't hold is refused (return false) and C#
// answers with a full snapshot.

import { rangeRingLabel } from './format.js';
import { esc } from './popupHelpers.js';
import { makeBoatSvg, makeRadarSvg } from './boatIcons.js';
import { toLayoutPoint, viewportSize } from './mapRotation.js';

let mapRef = null;
let colors = null;
let isSlowClient = false;
let getEditModeFlags = null;       // () => { routeEdit, polygonEdit, measure }
let editModeAddPoint = null;       // (mode, lat, lon) -> void
let flagUrl = null;
let rotateMarker = null;

// Sequence of the last applied update; -1 = holding nothing, so only a
// full snapshot is accepted.
let appliedSeq = -1;
// C# clock minus browser clock (ms) as of the last update, so the popup
// "N min ago" reads against the same clock that stamped lastAisSeen.
let clockOffsetMs = 0;

/**
 * Everything drawn for one vessel, keyed by SignalK context in
 * `vessels`. One record so add / remove / dispose can't leave a layer
 * behind (a stale COG vector, an orphaned trail).
 */
class VesselLayers {
    constructor(ctx) {
        this.ctx = ctx;
        /** Latest dynamic payload. */
        this.v = null;
        /** Latest identity fields (payload.static, kept across deltas). */
        this.s = null;
        this.marker = null;
        this.icon = null;
        this.pulse = false;
        /** Name label element inside the marker icon, and its text. */
        this.labelEl = null;
        this.labelText = null;
        this.vector = null;
        this.vectorColor = null;
        /** COG-vector tip drawn by the shared per-colour tip layer. */
        this.tipColor = null;
        this.tipEnd = null;
        /** Trail points as [lat, lon] tuples; drawn once there are two. */
        this.trailPts = [];
        this.trail = null;
        this.cpaOwnLine = null;
        this.cpaOwnX = null;
        this.cpaTgtLine = null;
        this.cpaTgtX = null;
        this.cpaSegment = null;
        /** Last CPA chip state, so unchanged pushes write nothing. */
        this.cpaSeverity = null;
        this.cpaLabel = null;
        this.cpaCompact = false;
    }

    removeVector() {
        safeRemoveLayer(this.vector);
        this.vector = null;
        this.vectorColor = null;
        if (this.tipColor) markTipDirty(this.tipColor);
        this.tipColor = null;
        this.tipEnd = null;
    }

    removeOwnSideCpa() {
        safeRemoveLayer(this.cpaOwnLine);
        safeRemoveLayer(this.cpaOwnX);
        safeRemoveLayer(this.cpaSegment);
        this.cpaOwnLine = null;
        this.cpaOwnX = null;
        this.cpaSegment = null;
        this.cpaSeverity = null;
        this.cpaLabel = null;
        this.cpaCompact = false;
    }

    removeCpaOverlay() {
        this.removeOwnSideCpa();
        safeRemoveLayer(this.cpaTgtLine);
        safeRemoveLayer(this.cpaTgtX);
        this.cpaTgtLine = null;
        this.cpaTgtX = null;
    }

    removeLabel() {
        if (!this.labelEl) return;
        this.labelEl.remove();
        this.labelEl = null;
        this.labelText = null;
    }

    dispose() {
        this.removeCpaOverlay();
        this.removeVector();
        safeRemoveLayer(this.trail);
        this.trail = null;
        this.trailPts = [];
        this.removeLabel();
        safeRemoveLayer(this.marker);
        this.marker = null;
    }
}

/** @type {Map<string, VesselLayers>} */
const vessels = new Map();

// COG-vector tip dots, one polyline per colour holding every tip of
// that colour as a dot (a two-point part at the tip with round caps):
// a circle marker per vessel would double the vector-layer count, and
// each vector layer is its own path element to lay out and paint.
const tipLayers = new Map();       // colour -> TipDotLine
const dirtyTipColors = new Set();
// Pixel offset given to the second point of each dot part after
// projection. Leaflet rounds projected points to whole pixels, so both
// points of a dot land on the same pixel, and the canvas renderer
// (preferCanvas on slow clients) prunes zero-length segments before
// stroking: no cap, no dot. A hundredth of a pixel is a real segment
// to canvas and SVG alike and invisible as length.
const TIP_DOT_NUDGE_PX = 0.01;

/**
 * True for a projected dot part whose two points coincide.
 *
 * Example: [{x:150,y:150},{x:150,y:150}] -> true;
 *          [{x:150,y:150},{x:151,y:150}] -> false
 */
export function isCollapsedDot(ring) {
    return ring.length === 2 && ring[0].x === ring[1].x && ring[0].y === ring[1].y;
}

// Polyline whose collapsed two-point parts get TIP_DOT_NUDGE_PX after
// every projection (zoom, reset), so the round caps always draw.
// Created on first use: L is a browser global and the pure helpers in
// this module also run under node:test.
let TipDotLine = null;
function createTipDotLine(parts, options) {
    if (!TipDotLine) {
        TipDotLine = L.Polyline.extend({
            _project() {
                L.Polyline.prototype._project.call(this);
                for (const ring of this._rings) {
                    if (isCollapsedDot(ring)) ring[1] = ring[1].add(L.point(TIP_DOT_NUDGE_PX, 0));
                }
            },
        });
    }
    return new TipDotLine(parts, options);
}

function markTipDirty(color) {
    dirtyTipColors.add(color);
}

/**
 * Tip-dot parts for the vessels drawing a tip in `color`: each tip as a
 * two-point part on itself (TipDotLine gives it length on screen).
 *
 * Example: tips [{color:'#a', end:[54,11]}, {color:'#b', end:[55,12]}], '#a'
 *       -> [[[54, 11], [54, 11]]]
 */
export function tipDotParts(tips, color) {
    const parts = [];
    for (const t of tips) {
        if (t.color === color) parts.push([t.end, t.end]);
    }
    return parts;
}

// Redraws the tip layers of every colour touched by this update.
function flushVectorTips() {
    if (dirtyTipColors.size === 0 || !mapRef) return;
    const tips = [];
    for (const rec of vessels.values()) {
        if (rec.tipColor) tips.push({ color: rec.tipColor, end: rec.tipEnd });
    }
    for (const color of dirtyTipColors) {
        const parts = tipDotParts(tips, color);
        let layer = tipLayers.get(color);
        if (parts.length === 0) {
            safeRemoveLayer(layer);
            tipLayers.delete(color);
        } else if (layer) {
            layer.setLatLngs(parts);
        } else {
            // Tip: "the boat is here at +N minutes" without the helm
            // extrapolating the dashes. smoothFactor 0 so Leaflet's
            // simplification keeps the two-point parts.
            layer = createTipDotLine(parts, {
                color, weight: 5, lineCap: 'round', smoothFactor: 0,
                interactive: false, pane: 'aisCogVectors',
            }).addTo(mapRef);
            tipLayers.set(color, layer);
        }
    }
    dirtyTipColors.clear();
}

// Name labels are hidden below this zoom: at harbour-overview scale a
// label per target turns into a wall of text over the chart.
const AIS_LABEL_MIN_ZOOM = 12;
let labelZoomOk = true;

// Helm-click selection for awareness-tier overlays: alarm-tier targets
// always draw their overlay, awareness targets only while selected.
// Clicking the selected target again clears it; clicking another
// awareness target moves the selection.
let selectedAwarenessCtx = null;

/**
 * Applies a C# trail update to the renderer's point list and returns
 * the new list. A full `trail` replaces the list; otherwise `trailTrim`
 * points (clamped to what is held) drop from the head and
 * `trailAppend` is appended. Flat [lat0, lon0, ...] arrays on the wire.
 *
 * Example: pts [[1,1],[2,2]], trailTrim 1, trailAppend [3,3]
 *       -> [[2,2],[3,3]]
 */
export function applyTrailUpdate(pts, trail, trailAppend, trailTrim) {
    if (trail) return unpackLatLonPairs(trail);
    let next = pts;
    if (trailTrim > 0) next = next.slice(Math.min(trailTrim, next.length));
    if (trailAppend && trailAppend.length >= 2) {
        if (next === pts) next = pts.slice();
        for (const p of unpackLatLonPairs(trailAppend)) next.push(p);
    }
    return next;
}

/**
 * Whether an update can be applied on top of the sequence currently
 * held. A full snapshot always applies.
 */
export function acceptsUpdate(heldSeq, update) {
    return update.baseSeq === -1 || update.baseSeq === heldSeq;
}

/**
 * Resolves a C# colour role ("ship" / "buddy" / "danger" / "radar")
 * against the live map palette; "ship" uses the vessel's ship-type
 * colour.
 */
export function toneColor(tone, shipColor, palette) {
    switch (tone) {
        case 'buddy': return palette.buddy;
        case 'danger': return palette.danger;
        case 'radar': return palette.radar;
        default: return shipColor;
    }
}

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
// Lazy-only: fetched on the first popup-open for an MMSI, never on
// updateAisTargets. In a busy harbour ~95% of popups are never opened.
//
// LRU cap: each settled data URI is ~3-10 KB, and a long passage
// through busy waters meets thousands of MMSIs. 200 entries is well
// above the popup history any helm cycles through; least-recently-USED
// entries (by popup-build read, not by fetch order) are evicted first
// via Map's insertion-order iteration.
const FLAG_CACHE_MAX = 200;
const flagPromiseCache = new Map();
const flagSettledCache = new Map();
let flagPlaceholderSeq = 0;

/** Re-insert key=>value to bump it to most-recent in Map's
 *  insertion-order iteration. No-op when the key isn't present.
 *  Skips the delete+set when the cache isn't near its cap - the
 *  delete+set pair triggers V8 internal-bucket rebalancing on the
 *  Map, which on a hot path adds up. Once we're within 75 %
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
// AIS icon size. 28 leaves own boat (30) visibly bigger while keeping
// traffic and its ship-type glyph legible on a helm screen.
const AIS_ICON_SIZE = 28;
const RADAR_ICON_SIZE = 26;

// Own-boat fix, pushed by the mux on every updatePosition tick.
let selfLat = 0, selfLon = 0;

// Helm-configured distance rings on own boat. Pure visual
// scaffolding ("how far is that pier?") with no alarm semantics
// and no link to the CPA / TCPA pipeline. Drawn as thin grey
// dashed circles so they read as a measurement tool rather than
// an alarm boundary. The Nth ring sits at baseNm × N, so the
// default 0.5 nm × 4 setup draws rings at 0.5 / 1.0 / 1.5 /
// 2.0 nm. Independent of harbor mode (helm-controlled). Default
// disabled - opt-in from Settings -> Display.
let distanceRingsEnabled = false;
let distanceRingsBaseNm = 0.5;
let distanceRingsCount = 4;
// Parallel arrays: distanceRings[i] is the L.circle, ...Labels[i] is
// the L.tooltip at its top. Length matches distanceRingsCount once
// drawDistanceRings has settled; the per-tick path in setBoatPosition
// reads .length so a partial mid-resize state is safe.
let distanceRings = [];
let distanceRingLabels = [];
// Pre-computed lat-degree north-offset per ring: divide ONCE on
// radius change, then per-tick
// repositioning is `lat + _distanceRingLatDegs[i]` - no division
// inside the boat-position hot path.
let _distanceRingLatDegs = [];

// Harbor-mode flag: hides name labels while set. Moored vessels are
// filtered C#-side (HarborAisFilter), so COG vectors and CPA overlays
// stay for the moving targets that remain.
let harborMode = false;

// Persistent helm preference for name labels, ANDed with !harborMode
// so leaving harbor mode doesn't resurrect labels hidden in Settings.
let aisLabelsVisible = true;

// Mirror of leafletInterop.followBoat. Kept here so the AIS popup-open
// path can pick a popup direction that opens AWAY from the nearest
// viewport edge instead of relying on Leaflet's autoPan, which would
// scroll the map and break follow-mode. leafletInterop is the source
// of truth; it pushes updates into setFollow() below whenever the helm
// toggles follow or a manual pan drops it.
let followBoat = true;

export function init(map, deps) {
    mapRef = map;
    colors = deps.colors;
    isSlowClient = deps.isSlowClient;
    getEditModeFlags = deps.getEditModeFlags;
    editModeAddPoint = deps.editModeAddPoint;
    flagUrl = deps.flagUrl;
    rotateMarker = deps.rotateMarker;

    // Custom pane for COG vectors so they stack above the CPA crossing
    // lines (overlayPane, zIndex 400) and below markers (600); sharing
    // the overlay pane let the long red CPA line hide the small COG
    // vector. Pointer events off so the pane never eats marker clicks.
    // Created idempotently in case init runs twice.
    if (!map.getPane('aisCogVectors')) {
        const pane = map.createPane('aisCogVectors');
        pane.style.zIndex = '410';
        pane.style.pointerEvents = 'none';
    }

    labelZoomOk = map.getZoom() >= AIS_LABEL_MIN_ZOOM;
    map.on('zoomend', onZoomEndLabels);
}

function onZoomEndLabels() {
    if (!mapRef) return;
    const ok = mapRef.getZoom() >= AIS_LABEL_MIN_ZOOM;
    if (ok === labelZoomOk) return;
    labelZoomOk = ok;
    rerenderLabels();
}

// Own-boat fix from the mux. The CPA overlay's own-side line starts
// here, and the distance rings (+ their labels) chase the boat.
export function setBoatPosition(lat, lon) {
    if (selfLat === lat && selfLon === lon) return;
    selfLat = lat;
    selfLon = lon;
    // Per-tick path is one add per ring, no division: the lat-degree
    // offsets are precomputed on radius change.
    for (let i = 0; i < distanceRings.length; i++) {
        distanceRings[i].setLatLng([lat, lon]);
        const lbl = distanceRingLabels[i];
        if (lbl) lbl.setLatLng([lat + _distanceRingLatDegs[i], lon]);
    }
}

// --- icons ---

function makeIcon(html, size) {
    return L.divIcon({ className: 'boat-icon', html, iconSize: [size, size], iconAnchor: [size/2, size/2] });
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
        const label = esc(category);
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
        return `<img class="ais-popup-flag" src="${esc(settled)}" alt="">`;
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
 * with an "(N min ago)" tail. ageSec is measured against the C# clock
 * (clockOffsetMs) at popup-build time, so both halves agree with the
 * clock that stamped the instant even when the tablet's clock drifts.
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
 * Builds the AIS popup HTML from a vessel record. Called lazily, on
 * popupopen and on updates while the popup is open: most of 200+
 * popups are never opened. All values come pre-resolved from C#.
 */
export function buildAisPopupHtml(rec) {
    const v = rec.v;
    const s = rec.s;
    const mmsi = s.mmsi || '';
    const fixed = (n, d) => (n != null ? n.toFixed(d) : '--');

    // "Last" row: age measured against the C# clock (see clockOffsetMs).
    const lastMs = Date.parse(v.lastAisSeen);
    const ageSec = Number.isNaN(lastMs) ? null : (Date.now() + clockOffsetMs - lastMs) / 1000;
    const lastAisHtml = formatLastAisSeenRow(v.lastAisSeen, ageSec);

    const subtitleHtml = s.subtitle
        ? `<div class="ais-popup-subtitle">${esc(s.subtitle)}</div>`
        : '';

    // CPA row, numbers bold and units regular so the eye latches on
    // the magnitudes. Present only for a closing target.
    let cpaHtml = '';
    if (v.cpaNm != null && v.tcpaMin != null) {
        const cls = v.cpaThreat === 'alarm' ? 'ais-popup-cpa-danger' : 'ais-popup-cpa';
        cpaHtml = `<tr><td>CPA</td><td class="${cls}">` +
            `<strong>${v.cpaNm.toFixed(2)}</strong>nm in ` +
            `<strong>${v.tcpaMin.toFixed(0)}</strong>min` +
            `</td></tr>`;
    }

    // COLREGS: role (the action the helm must take) over the
    // classification. The "?" opens the in-app quick reference via the
    // delegated data-ona-colregs handler. The label cell carries its own
    // class so "COLREGS ?" doesn't wrap the "?" onto its own line.
    let colregsHtml = '';
    if (v.colregsLabel) {
        const roleHtml = v.colregsRole
            ? `<div class="ais-popup-colregs-role ${v.colregsGiveWay ? 'ais-popup-colregs-give-way' : 'ais-popup-colregs-stand-on'}">${esc(v.colregsRole)}</div>`
            : '';
        const labelHtml = `<div class="ais-popup-colregs-label">${esc(v.colregsLabel)}</div>`;
        colregsHtml = `<tr>` +
            `<td class="ais-popup-colregs-label-cell">` +
                `COLREGS ` +
                `<a href="#" data-ona-colregs="1" class="ais-popup-colregs-help" ` +
                    `title="Open COLREGS quick reference">?</a>` +
            `</td>` +
            `<td class="ais-popup-colregs-cell">${roleHtml}${labelHtml}</td>` +
            `</tr>`;
    }

    // External lookups (free, no API key). VesselFinder's search page
    // takes ?name= even for MMSI queries.
    const mtUrl = mmsi ? `https://www.marinetraffic.com/en/ais/details/ships/mmsi:${esc(mmsi)}` : '';
    const vfUrl = mmsi ? `https://www.vesselfinder.com/vessels?name=${esc(mmsi)}` : '';

    // Buddy toggle sits in the title row; data-ona-buddy is caught by
    // the delegated handler on the map element.
    const buddyLabel = s.buddy ? '★ Remove buddy' : '☆ Add buddy';
    const buddyAttrs = `data-ona-buddy="1" data-ctx="${esc(v.context)}" data-mmsi="${esc(mmsi)}"`
        + ` data-nm="${esc(s.name || '')}" data-is="${s.buddy ? '1' : '0'}"`;
    const buddyHeaderHtml = mmsi
        ? `<a href="#" ${buddyAttrs} class="ais-popup-buddy">${buddyLabel}</a>`
        : '';
    const snoozeAttrs = `data-ona-snooze="1" data-ctx="${esc(v.context)}" data-nm="${esc(s.name || mmsi)}"`;
    const snoozeHtml = v.snoozable
        ? `<a href="#" ${snoozeAttrs} class="ais-popup-snooze">♫ Snooze alarm</a>`
        : '';

    let linksHtml = '';
    if (mmsi || snoozeHtml) {
        const rows = [];
        if (mmsi) {
            rows.push(
                `<div class="ais-popup-links-row">` +
                `<a href="${mtUrl}" target="_blank" rel="noopener" class="ais-popup-extern">MarineTraffic</a>` +
                `<a href="${vfUrl}" target="_blank" rel="noopener" class="ais-popup-extern">VesselFinder</a>` +
                `</div>`
            );
        }
        if (snoozeHtml) rows.push(`<div class="ais-popup-links-row">${snoozeHtml}</div>`);
        linksHtml = `<div class="ais-popup-footer">` + rows.join('') + `</div>`;
    }

    return (
        `<div class="ais-popup-content">` +
        `<div class="ais-popup-header">` +
            `<div class="ais-popup-title">${flagImgHtml(mmsi)}${esc(s.title)}</div>` +
            buddyHeaderHtml +
        `</div>` +
        subtitleHtml +
        `<table class="ais-popup-table">` +
          cpaHtml +
          colregsHtml +
          `<tr><td>SOG</td><td>${fixed(v.sogKn, 1)} kn</td></tr>` +
          `<tr><td>COG</td><td>${fixed(v.cogDeg, 0)}&deg;</td></tr>` +
          `<tr><td>HDG</td><td>${fixed(v.headingDeg, 0)}&deg;</td></tr>` +
          `<tr><td>Dist</td><td>${fixed(v.rangeNm, 2)} nm</td></tr>` +
          `<tr><td>BRG</td><td>${fixed(v.bearingDeg, 0)}&deg;</td></tr>` +
          lastAisHtml +
        `</table>` +
        linksHtml +
        `</div>`
    );
}

// --- main update ---

/**
 * Applies an AIS update from AisLayerController (full snapshot or delta).
 * Returns false when the update can't be applied, so C# resends a full
 * snapshot.
 */
export function updateAisTargets(update) {
    if (!mapRef || !update) return false;
    // A full rebuild mid-drag is the largest per-frame cost on a slow
    // client. Refuse; C# answers with a full snapshot next tick. Alarms
    // run C#-side off the delta stream, not off this marker state.
    if (isSlowClient && mapRef.dragging && mapRef.dragging._moving) {
        appliedSeq = -1;
        return false;
    }
    if (!acceptsUpdate(appliedSeq, update)) {
        appliedSeq = -1;
        return false;
    }

    const nowMs = Date.parse(update.nowUtc);
    if (!Number.isNaN(nowMs)) clockOffsetMs = nowMs - Date.now();

    if (update.baseSeq === -1) {
        // Full snapshot: anything not listed is gone.
        const listed = new Set(update.vessels.map(v => v.context));
        for (const [ctx, rec] of vessels) {
            if (!listed.has(ctx)) removeVessel(ctx, rec);
        }
    }
    for (const ctx of update.removed) {
        const rec = vessels.get(ctx);
        if (rec) removeVessel(ctx, rec);
    }
    try {
        for (const v of update.vessels) {
            let rec = vessels.get(v.context);
            if (!rec) {
                // A vessel's first appearance always carries its
                // identity fields; one without them would mean C# and JS
                // disagree on what is held.
                if (!v.static) { appliedSeq = -1; return false; }
                rec = new VesselLayers(v.context);
                vessels.set(v.context, rec);
            }
            renderVessel(rec, v);
        }
    } finally {
        flushVectorTips();
    }
    appliedSeq = update.seq;
    return true;
}

function removeVessel(ctx, rec) {
    rec.dispose();
    vessels.delete(ctx);
    if (selectedAwarenessCtx === ctx) selectedAwarenessCtx = null;
}

function renderVessel(rec, v) {
    if (v.static) rec.s = v.static;
    rec.v = v;
    const s = rec.s;

    renderMarker(rec, v, s);
    renderLabel(rec, s);

    // Popup on screen: refresh live so SOG / CPA / buddy label update
    // without a close-reopen.
    if (rec.marker.isPopupOpen()) {
        rec.marker.setPopupContent(buildAisPopupHtml(rec));
    }

    if (v.trail || v.trailAppend || v.trailTrim) {
        rec.trailPts = applyTrailUpdate(rec.trailPts, v.trail, v.trailAppend, v.trailTrim ?? 0);
        renderTrail(rec);
    }

    renderVector(rec, v, s);

    const showOverlay = v.cpaOverlay === 'alarm'
        || (v.cpaOverlay === 'awareness' && rec.ctx === selectedAwarenessCtx);
    if (showOverlay) renderCpaOverlay(rec);
    else rec.removeCpaOverlay();
}

function renderMarker(rec, v, s) {
    const color = toneColor(v.markerTone, s.shipColor, colors);
    const icon = s.sartCategory
        ? getSartIcon(s.sartCategory)
        : (s.radar ? getRadarIcon(color) : getAisIcon(color, s.glyphCategory));

    let marker = rec.marker;
    if (!marker) {
        marker = L.marker([v.lat, v.lon], { icon }).addTo(mapRef);
        rec.marker = marker;
        rec.icon = icon;
        marker._lastLat = v.lat;
        marker._lastLon = v.lon;
        bindMarker(rec, marker);
    } else {
        // setLatLng re-projects and writes a transform even for equal
        // input; setIcon detaches and re-attaches the DOM. The icon
        // caches return the same instance per (colour, category), so
        // identity tells "nothing to rebuild".
        if (marker._lastLat !== v.lat || marker._lastLon !== v.lon) {
            marker.setLatLng([v.lat, v.lon]);
            marker._lastLat = v.lat;
            marker._lastLon = v.lon;
        }
        if (rec.icon !== icon) {
            marker.setIcon(icon);
            rec.icon = icon;
            // The DOM was rebuilt; the pulse class goes with it.
            rec.pulse = false;
        }
    }
    rotateMarker(marker, v.rotationRad);

    const el = marker.getElement();
    if (!el) return;
    // classList.toggle and style writes invalidate style even when the
    // state is unchanged; write only on change.
    const pulse = !!v.pulse;
    if (rec.pulse !== pulse) {
        el.classList.toggle('cpa-pulse', pulse);
        rec.pulse = pulse;
    }
    const op = v.opacity ?? '';
    if (el.style.opacity !== op) el.style.opacity = op;
}

function bindMarker(rec, marker) {
    // During route / polygon / measure edit a tap on a vessel behaves
    // like a tap on water: add the point, don't open the popup.
    marker.on('click', (ev) => {
        const flags = getEditModeFlags();
        if (flags.routeEdit || flags.polygonEdit || flags.measure) {
            L.DomEvent.stopPropagation(ev);
            L.DomEvent.preventDefault(ev);
            const ll = ev.latlng || marker.getLatLng();
            // Route -> polygon -> measure priority for a tap on a vessel;
            // the measure-first override only applies to empty-map taps.
            if (flags.routeEdit)         editModeAddPoint('route', ll.lat, ll.lng);
            else if (flags.polygonEdit)  editModeAddPoint('polygon', ll.lat, ll.lng);
            else                         editModeAddPoint('measure', ll.lat, ll.lng);
            marker.closePopup();
            return;
        }
        toggleAwarenessOverlay(rec);
    });

    // maxWidth 420 keeps the MarineTraffic / VesselFinder / Snooze row
    // on one line on an iPad. autoPan with padding moves the map only
    // when the popup would leave the viewport; keepInView holds it there
    // if the helm then pans.
    marker.bindPopup('', {
        closeButton: false,
        maxWidth: 420,
        className: 'ais-popup',
        autoPan: true,
        autoPanPadding: [20, 20],
        keepInView: true,
    });
    // `preclick` runs ahead of Leaflet's click -> openPopup, so the
    // options set here apply to this open. In follow mode autoPan would
    // fight the boat-centred view: disable it and push the popup below
    // the marker when the marker sits in the top 30 % of the viewport.
    marker.on('preclick', () => {
        const popup = marker.getPopup();
        if (!popup) return;
        if (!followBoat || !mapRef) {
            popup.options.autoPan = true;
            popup.options.offset = L.point(0, 7);
            return;
        }
        popup.options.autoPan = false;
        const cp = mapRef.latLngToContainerPoint(marker.getLatLng());
        const onScreen = toLayoutPoint(cp.x, cp.y);
        popup.options.offset = onScreen.y < viewportSize().y * 0.3 ? L.point(0, 240) : L.point(0, 7);
    });
    marker.on('popupopen', () => {
        if (rec.v) marker.setPopupContent(buildAisPopupHtml(rec));
    });
}

// Name label: a child of the marker's icon element, positioned right
// of the chevron by inline style, so it moves with the marker's own
// transform. A permanent Leaflet tooltip per vessel read offsetWidth /
// offsetHeight on every reposition (each push and each pinch frame).
// The slot spans the icon height and starts at its right edge, so the
// icon centre sits at (-half icon width, 50%): the course-up counter-
// rotation (app.css, .ais-label-slot) pivots there.
function createLabelSlot() {
    const slot = document.createElement('div');
    slot.className = 'ais-label-slot';
    slot.style.cssText = 'position:absolute;left:100%;top:0;height:100%;display:flex;'
        + 'align-items:center;padding-left:4px;pointer-events:none;';
    const label = document.createElement('span');
    label.className = 'ais-label';
    slot.appendChild(label);
    return slot;
}

function renderLabel(rec, s) {
    const show = rec.v.labelVisible && !harborMode && aisLabelsVisible && labelZoomOk
        && !!s.displayName;
    const el = show ? rec.marker.getElement() : null;
    if (!el) {
        rec.removeLabel();
        return;
    }
    if (!rec.labelEl) rec.labelEl = createLabelSlot();
    // setIcon rewrites the icon element's innerHTML, dropping the slot.
    if (rec.labelEl.parentNode !== el) {
        el.appendChild(rec.labelEl);
        rec.labelEl.style.transformOrigin = `-${rec.icon.options.iconSize[0] / 2}px 50%`;
    }
    if (rec.labelText !== s.displayName) {
        // textContent: the name is shown verbatim, never parsed as HTML.
        rec.labelEl.firstChild.textContent = s.displayName;
        rec.labelText = s.displayName;
    }
}

function renderTrail(rec) {
    if (rec.trailPts.length < 2) {
        safeRemoveLayer(rec.trail);
        rec.trail = null;
        return;
    }
    if (!rec.trail) {
        // Dashed slate: reads as "where it has been", distinct from the
        // dashed identity-coloured COG vector pointing ahead.
        rec.trail = L.polyline(rec.trailPts, {
            color: '#94a3b8', weight: 1.2, opacity: 0.5,
            dashArray: '2,4', interactive: false,
        }).addTo(mapRef);
    } else {
        rec.trail.setLatLngs(rec.trailPts);
    }
}

function renderVector(rec, v, s) {
    if (v.vectorEndLat == null || v.vectorEndLon == null) {
        rec.removeVector();
        return;
    }
    const end = [v.vectorEndLat, v.vectorEndLon];
    const color = toneColor(v.vectorTone, s.shipColor, colors);
    if (!rec.vector) {
        rec.vector = L.polyline([[v.lat, v.lon], end], {
            color, weight: 1.5, dashArray: '6,4', pane: 'aisCogVectors',
        }).addTo(mapRef);
        rec.vectorColor = color;
    } else {
        rec.vector.setLatLngs([[v.lat, v.lon], end]);
        if (rec.vectorColor !== color) {
            rec.vector.setStyle({ color });
            rec.vectorColor = color;
        }
    }
    if (rec.tipColor !== color || rec.tipEnd[0] !== end[0] || rec.tipEnd[1] !== end[1]) {
        if (rec.tipColor && rec.tipColor !== color) markTipDirty(rec.tipColor);
        rec.tipColor = color;
        rec.tipEnd = end;
        markTipDirty(color);
    }
}

// --- CPA overlay ---

function cpaLabelHtml(rec) {
    const v = rec.v;
    return `<strong>${esc(rec.s.title)}</strong><br>${v.cpaNm.toFixed(2)}nm  T -${v.tcpaMin.toFixed(0)}′`;
}

function setCpaLine(line, from, to, color) {
    if (!line) {
        // Faint thin dashes: a 9-minute TCPA stretches half a mile of
        // line across the chart; the X and its label carry the emphasis.
        return L.polyline([from, to], {
            color, weight: 0.8, dashArray: '2,7', opacity: 0.5
        }).addTo(mapRef);
    }
    line.setLatLngs([from, to]);
    if (line.options.color !== color) line.setStyle({ color });
    return line;
}

// "×" marker at a closest-approach endpoint. Tapping it opens the
// target's popup and tooltip so the helm can drill in without finding
// the chevron first.
function setCpaX(m, latlon, severity, ctx) {
    if (!m) {
        m = L.marker(latlon, {
            icon: L.divIcon({
                className: `cpa-x-marker cpa-x-${severity}`,
                html: '<div class="cpa-x">×</div>',
                iconSize: [18, 18],
                iconAnchor: [9, 9],
            }),
            interactive: true,
            keyboard: false,
        }).addTo(mapRef);
        m._severity = severity;
        m.on('click', () => {
            m.openTooltip();
            vessels.get(ctx)?.marker?.openPopup();
        });
        return m;
    }
    m.setLatLng(latlon);
    if (m._severity !== severity) {
        const el = m.getElement();
        if (el) {
            el.classList.remove('cpa-x-danger', 'cpa-x-warn');
            el.classList.add(`cpa-x-${severity}`);
        }
        m._severity = severity;
    }
    return m;
}

/**
 * Closing-encounter overlay for one target: dashed projection lines
 * from both boats to their CPA points, an X at each, and a solid
 * segment between the Xs (the actual closest approach) carrying the
 * name / CPA / TCPA chip. Without an own CPA point only the target side
 * is drawn.
 */
function renderCpaOverlay(rec) {
    const v = rec.v;
    const isDanger = v.cpaOverlay === 'alarm';
    const severity = isDanger ? 'danger' : 'warn';
    const lineColor = isDanger ? colors.mob : colors.guardWarn;
    const tgtCpa = [v.cpaPointLat, v.cpaPointLon];

    rec.cpaTgtLine = setCpaLine(rec.cpaTgtLine, [v.lat, v.lon], tgtCpa, lineColor);
    rec.cpaTgtX = setCpaX(rec.cpaTgtX, tgtCpa, severity, rec.ctx);
    if (v.ownCpaPointLat == null || v.ownCpaPointLon == null) {
        rec.removeOwnSideCpa();
        return;
    }
    const ownCpa = [v.ownCpaPointLat, v.ownCpaPointLon];
    rec.cpaOwnLine = setCpaLine(rec.cpaOwnLine, [selfLat, selfLon], ownCpa, lineColor);
    rec.cpaOwnX = setCpaX(rec.cpaOwnX, ownCpa, severity, rec.ctx);

    let segment = rec.cpaSegment;
    if (!segment) {
        segment = L.polyline([ownCpa, tgtCpa], {
            color: lineColor, weight: 1.6, opacity: 0.85, interactive: true,
        }).addTo(mapRef);
        segment.on('click', () => {
            const m = vessels.get(rec.ctx)?.marker;
            if (m) { m.openTooltip(); m.openPopup(); }
        });
        rec.cpaSegment = segment;
    } else {
        segment.setLatLngs([ownCpa, tgtCpa]);
        if (segment.options.color !== lineColor) segment.setStyle({ color: lineColor });
    }

    // Chip on the segment when there's room, just above it when the two
    // CPA points sit closer than ~the chip width (zoomed out), so it
    // doesn't cover the line and both Xs. Re-evaluated every update.
    const p1 = mapRef.latLngToLayerPoint(L.latLng(ownCpa[0], ownCpa[1]));
    const p2 = mapRef.latLngToLayerPoint(L.latLng(tgtCpa[0], tgtCpa[1]));
    const tt = segment.getTooltip();
    const compact = cpaChipCompact(Math.hypot(p2.x - p1.x, p2.y - p1.y), tt ? rec.cpaCompact : null);
    const labelText = cpaLabelHtml(rec);
    if (!tt || compact !== rec.cpaCompact) {
        // Leaflet fixes a tooltip's direction at bind time.
        if (tt) segment.unbindTooltip();
        segment.bindTooltip(labelText, {
            permanent: isDanger,
            direction: compact ? 'top' : 'center',
            offset: compact ? [0, -6] : [0, 0],
            sticky: false,
            className: `cpa-label cpa-${severity}`,
        });
        rec.cpaCompact = compact;
        rec.cpaLabel = labelText;
        rec.cpaSeverity = severity;
        if (isDanger) segment.openTooltip();
        return;
    }
    // setContent reparses HTML and classList writes invalidate style even
    // when unchanged; most pushes change neither.
    if (rec.cpaLabel !== labelText) {
        tt.setContent(labelText);
        rec.cpaLabel = labelText;
    }
    if (rec.cpaSeverity !== severity) {
        tt.options.className = `cpa-label cpa-${severity}`;
        const ttEl = tt.getElement();
        if (ttEl) {
            ttEl.classList.remove('cpa-danger', 'cpa-warn');
            ttEl.classList.add(`cpa-${severity}`);
        }
        rec.cpaSeverity = severity;
    }
    // Escalation awareness -> alarm pins the chip open.
    if (tt.options.permanent !== isDanger) {
        tt.options.permanent = isDanger;
        if (isDanger) segment.openTooltip();
    }
}

// Chip placement hysteresis: compact (above the segment) under 70 px,
// on the segment over 90 px, unchanged in between, so a target hovering
// near one threshold doesn't rebind the tooltip on every push.
const CPA_COMPACT_ENTER_PX = 70;
const CPA_COMPACT_LEAVE_PX = 90;

/**
 * Whether the CPA chip sits above the segment (compact) for a segment
 * `lengthPx` long, given the previous placement (null: none yet, decide
 * on the midpoint).
 *
 * Example: (80, true) -> true; (80, false) -> false; (95, true) -> false
 */
export function cpaChipCompact(lengthPx, wasCompact) {
    if (lengthPx < CPA_COMPACT_ENTER_PX) return true;
    if (lengthPx > CPA_COMPACT_LEAVE_PX) return false;
    return wasCompact ?? lengthPx < (CPA_COMPACT_ENTER_PX + CPA_COMPACT_LEAVE_PX) / 2;
}

/**
 * Helm tap on a marker outside edit modes. Awareness-tier targets
 * toggle their overlay; tapping another awareness target moves the
 * selection. Draws immediately rather than on the next push.
 */
function toggleAwarenessOverlay(rec) {
    if (!rec.v || rec.v.cpaOverlay !== 'awareness') return;
    if (selectedAwarenessCtx === rec.ctx) {
        selectedAwarenessCtx = null;
        rec.removeCpaOverlay();
        return;
    }
    const prev = selectedAwarenessCtx ? vessels.get(selectedAwarenessCtx) : null;
    selectedAwarenessCtx = rec.ctx;
    if (prev && prev.v?.cpaOverlay !== 'alarm') prev.removeCpaOverlay();
    renderCpaOverlay(rec);
}

/** Pans the map to an AIS vessel and opens its popup. Returns true
 *  when a marker existed; false when the context didn't match anything
 *  (vessel aged out, AIS filter hiding it, deleted since the list
 *  rendered). The C# caller uses the return value to toast + restore
 *  follow so the user isn't left wondering why the tap did nothing. */
export function focusVessel(context) {
    const marker = vessels.get(context)?.marker;
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

// Best-effort layer removal that never throws: null layers are
// skipped, and a layer already gone from a re-initialised map must not
// turn a teardown into a rejected interop call.
function safeRemoveLayer(layer) {
    if (!layer || !mapRef) return;
    try { mapRef.removeLayer(layer); } catch (_) { /* already gone */ }
}

// Mirror of leafletInterop.setFollow. Called by the interop ladder
// whenever follow mode flips so the AIS popup-open path can pick a
// direction without autoPan when follow is on (autoPan would scroll
// the map and break follow).
export function setFollow(follow) { followBoat = !!follow; }

// Re-applies the label gate to every held vessel. Updates are deltas,
// so a vessel that doesn't change wouldn't otherwise pick up a toggle.
function rerenderLabels() {
    for (const rec of vessels.values()) {
        if (rec.marker && rec.v) renderLabel(rec, rec.s);
    }
}

// Persistent name-label preference.
export function setAisLabelsVisible(enabled) {
    aisLabelsVisible = !!enabled;
    rerenderLabels();
}

// Harbor mode hides name labels: hundreds of pontoon vessels stack into
// a wall of text.
export function setHarborMode(enabled) {
    harborMode = !!enabled;
    rerenderLabels();
}

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
    // Remove every layer from the map before dropping the references;
    // a route-edit -> re-init cycle otherwise leaks them into the old
    // map's layer registry.
    for (const rec of vessels.values()) rec.dispose();
    vessels.clear();
    for (const layer of tipLayers.values()) safeRemoveLayer(layer);
    tipLayers.clear();
    dirtyTipColors.clear();
    if (mapRef) mapRef.off('zoomend', onZoomEndLabels);
    selectedAwarenessCtx = null;
    appliedSeq = -1;
    clearDistanceRings();
    selfLat = 0; selfLon = 0;
    mapRef = null;
    colors = null;
    getEditModeFlags = null;
    editModeAddPoint = null;
    flagUrl = null;
    rotateMarker = null;
}
