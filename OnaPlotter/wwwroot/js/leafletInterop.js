// Leaflet JS interop for the chartplotter map.
// All map state lives here; Blazor calls exported functions via IJSRuntime.

import { RAD, DEG, NM_PER_METER, VECTOR_MINUTES, SPEED_BUCKETS,
         haversineMeters, bearingDeg, destPoint, vectorEnd,
         speedColor, speedBucket } from './geoMath.js';

let map = null;
let boatMarker = null;
let boatVector = null;
let vectorLabel = null;  // Time/distance label at end of COG vector.
let trackLayer = null;
let followBoat = true;
let mapOrientation = 'north'; // 'north', 'course', 'head'
let currentRotationDeg = 0;
let nightMode = false;
let dotNetRef = null;
let suppressMoveEnd = false;  // Suppress moveend during programmatic panTo.

// AIS state.
const aisMarkers = {};
const aisVectors = {};
const aisCpaOwnLines = {};  // polyline from own boat to own-CPA point, per vessel context
const aisCpaTgtLines = {};  // polyline from target to target-CPA point, per vessel context
const aisCpaLabels = {};    // tooltip at midpoint labelled CPA / TCPA
const aisTrailHistory = {}; // context -> [{lat, lon, t}, ...] (t = Date.now())
const aisTrailLines = {};   // context -> L.polyline
const AIS_TRAIL_SECONDS = 60;

// Best-effort external-lookup cache for vessels whose SignalK feed hasn't
// yet delivered a static-data AIS message (message 5 / 24). Keyed by MMSI.
// A value of null means "looked up and came back empty" — prevents endless
// retries. Bounded: on insert past VESSEL_NAME_CACHE_MAX we drop the oldest
// entry. A Map is used because its iteration is insertion-ordered, so the
// first key is the oldest, which is all we need for a simple LRU with
// promote-on-hit.
const VESSEL_NAME_CACHE_MAX = 500;
const vesselNameCache = new Map();
const vesselNameInflight = {};

function vesselNameCacheGet(mmsi) {
    if (!vesselNameCache.has(mmsi)) return undefined;
    const v = vesselNameCache.get(mmsi);
    // Promote: re-insert at the end so a recently-used entry isn't next to evict.
    vesselNameCache.delete(mmsi);
    vesselNameCache.set(mmsi, v);
    return v;
}

function vesselNameCacheSet(mmsi, name) {
    if (vesselNameCache.has(mmsi)) vesselNameCache.delete(mmsi);
    vesselNameCache.set(mmsi, name);
    while (vesselNameCache.size > VESSEL_NAME_CACHE_MAX) {
        const oldest = vesselNameCache.keys().next().value;
        vesselNameCache.delete(oldest);
    }
}

function vesselNameCacheHas(mmsi) { return vesselNameCache.has(mmsi); }

// Guard zone: collision-alarm envelope drawn around own boat.
let guardZoneRing = null;
let guardZoneRadiusNm = 0.5;       // default matches IAppSettings.CpaAlarmThreshold
let guardZoneLookaheadMin = 10;    // default matches IAppSettings.GuardZoneLookaheadMinutes
let guardZoneWarningFactor = 2.0;  // default matches IAppSettings.GuardZoneWarningFactor

// Shared bookkeeping for every by-id layer dict on the map: charts,
// routes, waypoints, notes, regions, and any future resource layer.
// The class only tracks the Leaflet layer and manages removal; call
// sites still call layer.addTo(map) themselves so they can stage
// layers inside layerGroups or control add-ordering. Keeps remove /
// clear consistent: `if (layer && map) map.removeLayer(layer)` gets
// written once, not six times.
class MarkerLayer {
    constructor() { this.items = {}; }
    has(id) { return Object.prototype.hasOwnProperty.call(this.items, id); }
    get(id) { return this.items[id]; }
    keys() { return Object.keys(this.items); }
    set(id, layer) { this.items[id] = layer; }
    remove(id) {
        const layer = this.items[id];
        if (layer) {
            if (map) map.removeLayer(layer);
            delete this.items[id];
        }
    }
    clear() {
        for (const id of Object.keys(this.items)) this.remove(id);
    }
}

// Chart layers from SignalK.
const chartLayers = new MarkerLayer();  // keyed by chart identifier
let osmBaseLayer = null;
let seaBaseLayer = null;

// Zoom-level badge (bottom-right). Assigned in initMap so the control
// exists before the first zoomend fires.
let zoomBadge = null;

// ---- Screen Wake Lock ------------------------------------------
// Browser API available in Chrome / Edge / recent Safari + iOS. The
// sentinel is held on the window object so a page navigation doesn't
// stack duplicate locks. Re-acquires on visibilitychange because iOS
// releases on background automatically.
let _wakeLockSentinel = null;
let _wakeLockWanted = false;

async function _acquireWakeLock() {
    if (!('wakeLock' in navigator)) return;
    if (_wakeLockSentinel) return;
    try {
        _wakeLockSentinel = await navigator.wakeLock.request('screen');
        _wakeLockSentinel.addEventListener('release', () => { _wakeLockSentinel = null; });
    } catch {
        // Permission denied, battery-saver, etc. -- nothing we can do.
        _wakeLockSentinel = null;
    }
}

function _releaseWakeLock() {
    const s = _wakeLockSentinel;
    _wakeLockSentinel = null;
    if (s) { try { s.release(); } catch { /* already released */ } }
}

// Public API called from C# when the user toggles the setting or the
// Map page mounts / unmounts.
export async function setWakeLock(on) {
    _wakeLockWanted = on;
    if (on) {
        await _acquireWakeLock();
        // iOS Safari releases the lock when the page is hidden; re-acquire
        // when we come back. Registered once; safe to re-register because
        // duplicates are no-ops in event-target semantics.
        if (!window._onaWakeLockVisibility) {
            window._onaWakeLockVisibility = () => {
                if (_wakeLockWanted && document.visibilityState === 'visible') {
                    _acquireWakeLock();
                }
            };
            document.addEventListener('visibilitychange', window._onaWakeLockVisibility);
        }
    } else {
        _releaseWakeLock();
    }
}

// Nautical-miles scale control. Leaflet bundles metric + imperial; the
// nautical scale is identical in shape but divides by 1852 m/nm. We
// subclass L.Control.Scale so we get the same "nice round number"
// rendering behaviour for free.
const NauticalScale = L.Control.Scale.extend({
    options: { metric: false, imperial: false, nautical: true },
    onAdd(map) {
        const className = 'leaflet-control-scale';
        const container = L.DomUtil.create('div', className);
        this._nauticalLine = L.DomUtil.create('div', 'leaflet-control-scale-line ona-scale-nm', container);
        map.on(this.options.updateWhenIdle ? 'moveend' : 'move', this._update, this);
        map.whenReady(this._update, this);
        return container;
    },
    _update() {
        if (!this._nauticalLine) return;
        const size = this._map.getSize();
        if (size.x <= 0) return;  // layout not settled -- Leaflet re-fires on move
        const bounds = this._map.getBounds();
        const centerLat = bounds.getCenter().lat;
        const halfWorldMeters = 6378137 * Math.PI * Math.cos(centerLat * Math.PI / 180);
        const dist = halfWorldMeters * (bounds.getNorthEast().lng - bounds.getSouthWest().lng) / 180;
        const maxMeters = dist * (this.options.maxWidth / size.x);
        if (!isFinite(maxMeters) || maxMeters <= 0) return;
        const nm = maxMeters / 1852;
        const d = this._getRoundNum(nm);
        if (!isFinite(d) || d <= 0) return;
        this._nauticalLine.style.width = ((d * 1852) / maxMeters * this.options.maxWidth) + 'px';
        // cbl (cables) = 0.1 nm. Below 1 nm the bar is small enough that a
        // more granular unit reads better than "0.5 nm".
        this._nauticalLine.innerHTML = d < 1 ? `${(d * 10).toFixed(0)} cbl` : `${d} nm`;
    },
    onRemove(map) {
        // Avoid leaking the move listener when the control (or map) is torn
        // down. Leaflet's built-in L.Control.Scale does the same.
        map.off(this.options.updateWhenIdle ? 'moveend' : 'move', this._update, this);
    }
});

// Zoom-level badge. Shows "z N" at glance, flips to amber with
// "z N (native K)" when the map is zoomed past the top chart's native
// max so sailors know tiles are being scaled up rather than fresh.
const ZoomBadge = L.Control.extend({
    onAdd() {
        this._el = L.DomUtil.create('div', 'ona-zoom-badge');
        L.DomEvent.disableClickPropagation(this._el);
        return this._el;
    },
    update() {
        if (!this._el || !this._map) return;
        const z = this._map.getZoom();
        let topNative = 0;
        for (const native of chartNativeMax.values()) {
            if (native > topNative) topNative = native;
        }
        if (topNative > 0 && z > topNative) {
            this._el.textContent = `z ${z} (native ${topNative})`;
            this._el.classList.add('ona-zoom-badge-over');
        } else {
            this._el.textContent = `z ${z}`;
            this._el.classList.remove('ona-zoom-badge-over');
        }
    }
});

// Routes and server track.
const routeLayers = new MarkerLayer();  // keyed by route ID
let serverTrackLayer = null;

// Active route navigation.
let activeRouteLayer = null;   // L.layerGroup: full route polyline + waypoint markers
let activeRouteCoords = null;  // [[lat, lon], ...] cached for WP index lookup
let nextWpMarker = null;       // Pulsing marker at next waypoint
let courseLineLeg = null;       // Polyline: previous WP to next WP
let courseLineBearing = null;   // Polyline: boat to next WP
let courseLineXte = null;       // Polyline: XTE perpendicular tick

// Laylines.
let laylineStarboard = null;    // Green polyline from boat
let laylinePort = null;         // Red polyline from boat
let laylineWpStarboard = null;  // Green polyline from waypoint (dimmer)
let laylineWpPort = null;       // Red polyline from waypoint (dimmer)

// Bearing/distance tool.
let bearingLine = null;
let bearingLabel = null;

// MOB state.
let mobMarker = null;
let mobCircle = null;
let mobLine = null;
let mobLabel = null;

// Anchor watch state.
let anchorMarker = null;
let anchorCircle = null;
// Swing-arc history: own-boat positions sampled while the anchor is set,
// trimmed to ANCHOR_TRAIL_MINUTES so the captain can see at a glance how
// much water the boat has actually covered on this tide cycle.
const anchorTrail = [];
let anchorTrailLayer = null;
const ANCHOR_TRAIL_MINUTES = 60;
const ANCHOR_TRAIL_SAMPLE_MS = 10_000;

// Own vessel state cache (for CPA calculations).
let selfLat = 0, selfLon = 0, selfCogRad = null, selfSogMs = null;

// HTML-escape untrusted strings for popup content.
function esc(s) { const d = document.createElement('div'); d.textContent = s; return d.innerHTML; }

// --- Icons ---

// Boat icon: clean chevron shape, subtle drop shadow for depth.
// `category` (optional) is a coarse ship-type bucket used to draw a
// small glyph inside the chevron so vessel type is distinguishable
// without colour (accessibility / colour-blind users). Four buckets:
//   - "sail":       diamond (sailing, pleasure)
//   - "fish":       crossed nets (fishing)
//   - "commercial": solid dot (cargo, tanker, passenger, tug)
//   - "service":    cross/plus (military, sar)
// Own-boat and unknown vessels get no glyph.
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
// so it never obscures the outline; stroke colour is a fixed dark so it
// reads on any coloured chevron (including muted warms). The category
// string comes from C# (Utilities/AisPalette.ShipTypeCategory); this
// function is a pure renderer that maps a category to SVG.
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

// Magenta stands out against the blue water on OpenSeaMap/OSM tiles and
// doesn't collide with AIS ship-type palettes (greens/blues) or the reds
// reserved for MOB and collision alarms.
const selfIcon = makeIcon(makeBoatSvg('#ec4899', 30, true), 30);

// Two palette entries that JS still needs: danger overrides the
// C#-resolved ship-type colour when a CPA alarm is active for a
// vessel, and buddy likewise when the buddy-list-plugin marks the
// target as a friend. Both mirror entries in Utilities/AisPalette.cs
// (palette source of truth) -- keep these in sync when the palette
// changes, or thread isDanger/isBuddy resolution entirely through C#.
const AIS_DANGER_COLOR = '#c4453e';
const AIS_BUDDY_COLOR  = '#e9c46a';

// Radar ARPA icon: outline triangle (no fill) + small center dot.
// Classic ARPA look, and visually distinct from the filled AIS chevron.
// Colour stays in the same warm family (tan) so radar targets read as
// "same chart, different source" rather than "new palette".
const RADAR_COLOR = '#b08d5a';
function makeRadarSvg(fill, size) {
    const s = size || 22;
    const h = s / 2;
    return `<svg width="${s}" height="${s}" viewBox="-${h} -${h} ${s} ${s}" `
         + `style="filter:drop-shadow(0 1px 2px rgba(0,0,0,0.4))">`
         + `<polygon points="0,-${h-3} ${h-4},${h-5} -${h-4},${h-5}" `
         + `fill="none" stroke="${fill}" stroke-width="1.6" stroke-linejoin="round" opacity="0.95"/>`
         + `<circle cx="0" cy="0" r="1.6" fill="${fill}"/></svg>`;
}

// Icon caches (one per source x colour x category combo).
const aisIconCache = {};
const radarIconCache = {};
const sartIconCache = {};
function getAisIcon(color, category) {
    const key = `${color}|${category || ''}`;
    if (!aisIconCache[key]) {
        aisIconCache[key] = makeIcon(makeBoatSvg(color, 24, false, category), 24);
    }
    return aisIconCache[key];
}
function getRadarIcon(color) {
    if (!radarIconCache[color]) {
        radarIconCache[color] = makeIcon(makeRadarSvg(color, 22), 22);
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

// AIS name labels (tooltips).
const aisLabels = {};

const mobIcon = L.divIcon({
    className: 'mob-icon',
    html: '<div class="mob-pulse"></div>',
    iconSize: [20, 20],
    iconAnchor: [10, 10]
});

function rotateMarker(marker, rad) {
    if (rad == null) return;
    const el = marker.getElement();
    if (!el) return;
    const svg = el.querySelector('svg');
    if (svg) svg.style.transform = `rotate(${rad * DEG}deg)`;
}

// Fullscreen control helpers.
function fullscreenIconSvg(isFs) {
    if (isFs) {
        // "exit fullscreen" icon: four inward-pointing corners
        return '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M8 3v4a1 1 0 0 1-1 1H3"/><path d="M21 8h-4a1 1 0 0 1-1-1V3"/><path d="M3 16h4a1 1 0 0 1 1 1v4"/><path d="M16 21v-4a1 1 0 0 1 1-1h4"/></svg>';
    }
    // "enter fullscreen" icon: four outward-pointing corners
    return '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 8V5a2 2 0 0 1 2-2h3"/><path d="M16 3h3a2 2 0 0 1 2 2v3"/><path d="M21 16v3a2 2 0 0 1-2 2h-3"/><path d="M8 21H5a2 2 0 0 1-2-2v-3"/></svg>';
}

function isInFullscreen() {
    return !!(document.fullscreenElement || document.webkitFullscreenElement
        || document.querySelector('.map-container.map-fullscreen'));
}

function syncFsIcon() {
    if (window._onaFsBtn) window._onaFsBtn.innerHTML = fullscreenIconSvg(isInFullscreen());
}

function toggleFullscreenFromControl() {
    const container = document.querySelector('.map-container');
    if (!container) return;
    // iOS browsers (Safari, Firefox, Chrome) all route through WebKit and
    // have spotty native Fullscreen support on non-video elements. Treat
    // the CSS class as the source of truth; native API is a nice-to-have
    // upgrade that we kick off best-effort.
    const currently = container.classList.contains('map-fullscreen') || isInFullscreen();

    if (!currently) {
        cssFsToggle(true);
        try {
            const p = container.requestFullscreen
                ? container.requestFullscreen()
                : container.webkitRequestFullscreen && container.webkitRequestFullscreen();
            if (p && typeof p.catch === 'function') p.catch(() => { /* ignore */ });
        } catch { /* ignore - CSS still covers us */ }
    } else {
        cssFsToggle(false);
        try {
            const p = document.exitFullscreen
                ? document.exitFullscreen()
                : document.webkitExitFullscreen && document.webkitExitFullscreen();
            if (p && typeof p.catch === 'function') p.catch(() => { /* ignore */ });
        } catch { /* ignore */ }
    }
    setTimeout(() => {
        if (map) map.invalidateSize();
        syncFsIcon();
    }, 200);
}

function cssFsToggle(on) {
    const container = document.querySelector('.map-container');
    if (!container) return;
    if (on) container.classList.add('map-fullscreen');
    else container.classList.remove('map-fullscreen');
}

// ========== EXPORTED FUNCTIONS ==========

export function initMap(elementId, lat, lon, zoom, dotNetObjRef) {
    if (map) map.remove();
    dotNetRef = dotNetObjRef;

    // maxZoom 22 lets chart tiles overzoom past their native limit (we
    // set maxNativeZoom on chart layers to cap the tile fetch and then
    // let Leaflet scale the last valid tile up). Base OSM still caps at
    // its own maxZoom via the per-layer option, so we don't hit 404s.
    map = L.map(elementId, { zoomControl: false, maxZoom: 22 }).setView([lat, lon], zoom);
    // Drop the "Leaflet |" prefix from the attribution bar. The actual
    // OSM / OpenSeaMap attribution stays (ODbL / CC-BY-SA require it);
    // the Leaflet credit is courtesy and removable.
    map.attributionControl.setPrefix(false);

    // Zoom control in top-right to avoid HUD overlap.
    L.control.zoom({ position: 'topright' }).addTo(map);

    // Custom fullscreen control next to zoom. Uses native API with webkit
    // fallback, and a CSS-only fallback via a class toggle for iOS browsers
    // (Safari / Firefox / Chrome, all WebKit under the hood).
    const FullscreenControl = L.Control.extend({
        options: { position: 'topright' },
        onAdd: function () {
            const container = L.DomUtil.create('div', 'leaflet-bar leaflet-control ona-fs-control');
            // Real <button>, not an anchor. Firefox iOS has quirks with
            // <a href="#"> inside Leaflet controls where preventDefault
            // races the page-scroll-to-top behaviour.
            const btn = L.DomUtil.create('button', 'ona-fs-btn', container);
            btn.type = 'button';
            btn.title = 'Fullscreen';
            btn.setAttribute('aria-label', 'Toggle fullscreen');
            btn.innerHTML = fullscreenIconSvg(false);
            // Bind both click and touchend so iPadOS fires on first tap
            // without waiting for the synthetic-click fallback.
            const fire = (e) => {
                L.DomEvent.preventDefault(e);
                L.DomEvent.stopPropagation(e);
                toggleFullscreenFromControl();
            };
            L.DomEvent.on(btn, 'click', fire);
            L.DomEvent.on(btn, 'touchend', fire);
            L.DomEvent.disableClickPropagation(container);
            L.DomEvent.disableScrollPropagation(container);
            window._onaFsBtn = btn;
            return container;
        }
    });
    new FullscreenControl().addTo(map);

    // Listen for native fullscreen changes to keep the icon in sync.
    document.addEventListener('fullscreenchange', syncFsIcon);
    document.addEventListener('webkitfullscreenchange', syncFsIcon);

    // Scale bars + zoom badge all sit bottom-left, stacked, so a helm
    // glance gets "how far is that dot / am I overzoomed" in one place.
    // Metric ON (km/m) + Nautical ON (custom subclass below, since
    // Leaflet's built-in is only metric / imperial). Imperial OFF --
    // nobody plots in miles at sea.
    L.control.scale({ metric: true, imperial: false, maxWidth: 140, position: 'bottomleft' }).addTo(map);
    new NauticalScale({ position: 'bottomleft', maxWidth: 140 }).addTo(map);

    // Zoom-level badge: "z 14" chip, amber when the top chart is
    // overzooming (map zoom > native) so the helmsman knows tiles are
    // stretched. Bottom-left with the scale bars so all chart-scale
    // context lives in one corner.
    zoomBadge = new ZoomBadge({ position: 'bottomleft' });
    zoomBadge.addTo(map);
    map.on('zoomend', () => zoomBadge.update());
    zoomBadge.update();

    // keepBuffer bumped above the default 2 so tiles stay in memory a
    // few rings further out; panning back doesn't re-request. Cheap
    // memory, noticeable smoothness on the Pi-local-wifi setup where
    // re-fetch RTT is low but visible.
    // updateWhenIdle=false: stream tiles during pan (not only at pan-
    // end). Leaflet's default flips to true on mobile user-agents; we
    // override because our "mobile" is an iPad in the cockpit where
    // the user wants the map to feel alive, not freeze mid-drag.
    osmBaseLayer = L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
        maxNativeZoom: 19,
        maxZoom: 22,
        keepBuffer: 4,
        updateWhenIdle: false,
        crossOrigin: 'anonymous',
        attribution: '&copy; OpenStreetMap contributors',
        referrerPolicy: 'strict-origin-when-cross-origin'
    }).addTo(map);

    seaBaseLayer = L.tileLayer('https://tiles.openseamap.org/seamark/{z}/{x}/{y}.png', {
        maxNativeZoom: 19,
        maxZoom: 22,
        keepBuffer: 4,
        updateWhenIdle: false,
        crossOrigin: 'anonymous',
        attribution: '&copy; OpenSeaMap',
        opacity: 0.8,
        referrerPolicy: 'strict-origin-when-cross-origin'
    }).addTo(map);

    // featureGroup (not layerGroup) so zoomToTrack can call getBounds() on it.
    trackLayer = L.featureGroup().addTo(map);
    boatMarker = L.marker([lat, lon], { icon: selfIcon, zIndexOffset: 1000 }).addTo(map);
    // Bind an empty popup and rebuild its content on every open so the
    // numbers match the current NavigationData snapshot rather than a
    // frozen one from the last click. updatePosition passes lat/lon and
    // the other fields; we stash them on the marker for popupopen to
    // read without closing over state that might drift.
    boatMarker.bindPopup('', { className: 'ais-popup', maxWidth: 260 });
    boatMarker.on('popupopen', () => {
        const data = boatMarker._onaSelfData || {};
        boatMarker.setPopupContent(buildSelfPopupHtml(data));
    });
    boatVector = L.polyline([], { color: '#f9a8d4', weight: 1.5, dashArray: '6,4', opacity: 0.8 }).addTo(map);

    // Map click: in route edit mode, add waypoint. In measurement
    // mode, drop a measurement point. Otherwise just dismiss menus.
    map.on('click', (e) => {
        if (routeEditMode) {
            addEditWaypoint(e.latlng.lat, e.latlng.lng);
            return;
        }
        if (polygonEditMode) {
            addPolygonVertexInternal(e.latlng.lat, e.latlng.lng);
            return;
        }
        if (measureActive) {
            addMeasurePoint(e.latlng.lat, e.latlng.lng);
            return;
        }
        if (dotNetRef) dotNetRef.invokeMethodAsync('OnDismissContextMenu');
        clearBearingLine();
    });
    // Double-click: toggle bearing/distance measurement line.
    map.on('dblclick', (e) => {
        L.DomEvent.stopPropagation(e);
        if (bearingLine) clearBearingLine();
        else drawBearingLine(e.latlng.lat, e.latlng.lng);
    });

    // Right-click (desktop) and long-press (touch) -> context menu callback to Blazor.
    function showContextMenu(latlng) {
        if (routeEditMode || polygonEditMode || !dotNetRef) return;
        const pt = map.latLngToContainerPoint(latlng);
        const sz = map.getSize();
        const x = Math.min(pt.x, sz.x - 175);
        const y = Math.min(pt.y, sz.y - 125);
        dotNetRef.invokeMethodAsync('OnMapContextMenu', latlng.lat, latlng.lng, Math.max(x, 5), Math.max(y, 5));
    }

    map.on('contextmenu', (e) => {
        e.originalEvent.preventDefault();
        showContextMenu(e.latlng);
    });

    // Long-press for touch devices. Tuned to avoid firing during an
    // intentional pan: need a full 700 ms of stillness and a tight 8 px
    // movement budget, and Leaflet's own dragstart cancels immediately so
    // a two-finger pinch or one-finger drag never accidentally triggers it.
    const LONG_PRESS_MS = 700;
    const LONG_PRESS_MAX_MOVE_PX = 8;
    let longPressTimer = null;
    let longPressStartPt = null;
    const mapEl = map.getContainer();
    const cancelLongPress = () => {
        if (longPressTimer) { clearTimeout(longPressTimer); longPressTimer = null; }
        longPressStartPt = null;
    };
    map.on('movestart dragstart zoomstart', cancelLongPress);

    mapEl.addEventListener('touchstart', (e) => {
        cancelLongPress();
        if (e.touches.length !== 1) return; // two-finger pinch, etc.
        // Ignore presses that land on a control (buttons, map chrome).
        if (e.target && e.target.closest && e.target.closest('.leaflet-control, button, a, input, label'))
            return;
        const t0 = e.touches[0];
        longPressStartPt = { x: t0.clientX, y: t0.clientY };
        longPressTimer = setTimeout(() => {
            longPressTimer = null;
            if (!longPressStartPt) return;
            const rect = mapEl.getBoundingClientRect();
            const latlng = map.containerPointToLatLng([
                longPressStartPt.x - rect.left,
                longPressStartPt.y - rect.top
            ]);
            if (navigator.vibrate) navigator.vibrate(30);
            showContextMenu(latlng);
            longPressStartPt = null;
        }, LONG_PRESS_MS);
    }, { passive: true });
    mapEl.addEventListener('touchmove', (e) => {
        if (!longPressTimer || !longPressStartPt || e.touches.length !== 1) return;
        const dx = e.touches[0].clientX - longPressStartPt.x;
        const dy = e.touches[0].clientY - longPressStartPt.y;
        if (dx * dx + dy * dy > LONG_PRESS_MAX_MOVE_PX * LONG_PRESS_MAX_MOVE_PX)
            cancelLongPress();
    }, { passive: true });
    mapEl.addEventListener('touchend', cancelLongPress, { passive: true });
    mapEl.addEventListener('touchcancel', cancelLongPress, { passive: true });

    // Delegated click handler: AIS popup buddy / snooze links tag
    // themselves with data-ona-* so we can route them to Blazor without
    // leaking a callback through each popup's HTML. One listener handles
    // both actions; the closest() selector filters.
    mapEl.addEventListener('click', (e) => {
        if (!dotNetRef || !e.target || !e.target.closest) return;
        const buddy = e.target.closest('a[data-ona-buddy]');
        if (buddy) {
            e.preventDefault();
            e.stopPropagation();
            dotNetRef.invokeMethodAsync('OnToggleBuddy',
                buddy.getAttribute('data-ctx') || '',
                buddy.getAttribute('data-mmsi') || null,
                buddy.getAttribute('data-nm') || null,
                buddy.getAttribute('data-is') === '1');
            return;
        }
        const snooze = e.target.closest('a[data-ona-snooze]');
        if (snooze) {
            e.preventDefault();
            e.stopPropagation();
            dotNetRef.invokeMethodAsync('OnSnoozeVessel',
                snooze.getAttribute('data-ctx') || '',
                snooze.getAttribute('data-nm') || '');
            return;
        }
    });

    // Notify Blazor when the viewport changes so layers can be filtered by bounds.
    // Debounced: skip events caused by programmatic panTo (follow mode) and coalesce
    // rapid user interactions into a single callback.
    let boundsTimer = null;
    map.on('moveend', () => {
        if (!dotNetRef || suppressMoveEnd) return;
        clearTimeout(boundsTimer);
        boundsTimer = setTimeout(() => {
            const b = map.getBounds();
            dotNetRef.invokeMethodAsync('OnMapBoundsChanged',
                b.getWest(), b.getSouth(), b.getEast(), b.getNorth());
        }, 300);
    });
}

export function updatePosition(lat, lon, headingRad, cogRad, sogMs) {
    if (!map || !boatMarker) return;

    selfLat = lat; selfLon = lon; selfCogRad = cogRad; selfSogMs = sogMs;

    // Stash the latest snapshot on the marker so popupopen can render
    // fresh numbers without a closed-over stale copy.
    boatMarker._onaSelfData = { lat, lon, headingRad, cogRad, sogMs };

    boatMarker.setLatLng([lat, lon]);
    rotateMarker(boatMarker, headingRad ?? cogRad);

    if (guardZoneRing) guardZoneRing.setLatLng([lat, lon]);
    updateAnchorTrail(lat, lon);

    const end = vectorEnd(lat, lon, cogRad, sogMs);
    if (end) {
        boatVector.setLatLngs([[lat, lon], end]);
        // Label at vector tip: time and distance.
        const distNm = (sogMs * VECTOR_MINUTES * 60) * NM_PER_METER;
        const label = `${VECTOR_MINUTES}min / ${distNm.toFixed(1)}nm`;
        if (vectorLabel) {
            vectorLabel.setLatLng(end);
            vectorLabel.setContent(label);
        } else {
            vectorLabel = L.tooltip({
                permanent: true, direction: 'right', offset: [6, 0],
                className: 'vector-label'
            }).setLatLng(end).setContent(label).addTo(map);
        }
    } else {
        boatVector.setLatLngs([]);
        if (vectorLabel) { map.removeLayer(vectorLabel); vectorLabel = null; }
    }

    if (followBoat) {
        suppressMoveEnd = true;
        map.panTo([lat, lon], { animate: true, duration: 0.5 });
        setTimeout(() => { suppressMoveEnd = false; }, 600);
    }

    // Apply map rotation for course-up / head-up modes.
    if (mapOrientation === 'course' && cogRad != null) {
        applyMapRotation(cogRad * DEG);
    } else if (mapOrientation === 'head' && headingRad != null) {
        applyMapRotation(headingRad * DEG);
    }

    // Update MOB line if active.
    if (mobMarker) {
        const mll = mobMarker.getLatLng();
        const dist = haversineMeters(lat, lon, mll.lat, mll.lng) * NM_PER_METER;
        const brg = bearingDeg(lat, lon, mll.lat, mll.lng);
        if (mobLine) mobLine.setLatLngs([[lat, lon], [mll.lat, mll.lng]]);
        if (mobLabel) {
            mobLabel.setLatLng([(lat + mll.lat)/2, (lon + mll.lng)/2]);
            mobLabel.setContent(`${brg.toFixed(0)}&deg; / ${dist.toFixed(2)} nm`);
        }
    }

    // Update anchor watch.
    if (anchorMarker && anchorCircle) {
        const all = anchorMarker.getLatLng();
        const dist = haversineMeters(lat, lon, all.lat, all.lng);
        const inside = dist <= anchorCircle.getRadius();
        anchorCircle.setStyle({ color: inside ? '#22c55e' : '#ef4444', fillColor: inside ? '#22c55e' : '#ef4444' });
    }
}

// Pending points buffer for batched track updates.
let pendingTrackPoints = [];

export function setColoredTrack(points) {
    if (!trackLayer) return;
    trackLayer.clearLayers();
    if (points.length < 2) return;

    // Group consecutive segments by speed bucket, emit one polyline per run.
    let runBucket = speedBucket(points[1][2]);
    let runCoords = [[points[0][0], points[0][1]]];

    for (let i = 1; i < points.length; i++) {
        const b = speedBucket(points[i][2]);
        const coord = [points[i][0], points[i][1]];

        if (b !== runBucket) {
            // Flush current run.
            runCoords.push(coord); // Bridge point.
            L.polyline(runCoords, { color: speedColor(SPEED_BUCKETS[runBucket]), weight: 2.5, opacity: 0.8 }).addTo(trackLayer);
            runBucket = b;
            runCoords = [coord];
        } else {
            runCoords.push(coord);
        }
    }
    // Flush last run.
    if (runCoords.length >= 2) {
        L.polyline(runCoords, { color: speedColor(SPEED_BUCKETS[runBucket]), weight: 2.5, opacity: 0.8 }).addTo(trackLayer);
    }
}

export function addColoredTrackPoint(lat, lon, sogMs, prevLat, prevLon) {
    // Buffer points and flush as a batch every N points to reduce interop calls.
    pendingTrackPoints.push([lat, lon, sogMs, prevLat, prevLon]);
    if (pendingTrackPoints.length >= 10) flushTrackPoints();
}

const MAX_TRACK_SEGMENTS = 1000;

export function flushTrackPoints() {
    if (!trackLayer || pendingTrackPoints.length === 0) return;
    for (const [lat, lon, sogMs, pLat, pLon] of pendingTrackPoints) {
        L.polyline([[pLat, pLon], [lat, lon]], {
            color: speedColor(sogMs), weight: 2.5, opacity: 0.8
        }).addTo(trackLayer);
    }
    pendingTrackPoints = [];

    // Prune oldest segments to prevent unbounded DOM growth.
    const layers = trackLayer.getLayers();
    if (layers.length > MAX_TRACK_SEGMENTS) {
        const excess = layers.length - MAX_TRACK_SEGMENTS;
        for (let i = 0; i < excess; i++) {
            trackLayer.removeLayer(layers[i]);
        }
    }
}

export function updateAisTargets(vessels) {
    if (!map) return;
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
        const isDanger = cpaInfo
            && cpaInfo.cpa < guardZoneRadiusNm
            && cpaInfo.tcpa < guardZoneLookaheadMin
            && cpaInfo.tcpa > 0;
        // Within guardZone*factor we draw an amber warning line; this gives
        // the captain situational awareness before a red alarm fires. The
        // factor is user-configurable via Settings.GuardZoneWarningFactor.
        // Buddies never trigger the danger/warning overlays - they're
        // intentionally sailing near us and shouldn't paint the map red.
        const isDangerEff = isDanger && !v.buddy;
        const isWarning = !isDangerEff && !v.buddy && cpaInfo
            && cpaInfo.cpa < guardZoneRadiusNm * guardZoneWarningFactor
            && cpaInfo.tcpa < guardZoneLookaheadMin * guardZoneWarningFactor
            && cpaInfo.tcpa > 0;
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
            color = isDangerEff ? AIS_DANGER_COLOR : RADAR_COLOR;
        } else if (v.buddy) {
            color = AIS_BUDDY_COLOR;              // buddies always win
        } else if (isDangerEff) {
            color = AIS_DANGER_COLOR;             // CPA alarm active
        } else {
            color = v.shipColor || '#e0c9a6';     // palette default from C#
        }
        const category = isRadar ? null : v.glyphCategory;
        const icon = isSart
            ? getSartIcon(v.sartCategory)
            : (isRadar ? getRadarIcon(color) : getAisIcon(color, category));

        let marker = aisMarkers[v.context];
        if (!marker) {
            marker = L.marker([v.lat, v.lon], { icon }).addTo(map);
            aisMarkers[v.context] = marker;
        } else {
            marker.setLatLng([v.lat, v.lon]);
            marker.setIcon(icon);
        }
        if (!isSart) rotateMarker(marker, v.cogRad ?? v.headingRad);

        // Pulse an expanding red ring around any AIS / radar target
        // whose CPA is in the "danger" band (matches the AIS_DANGER_COLOR
        // tint on the chevron). Adds a .cpa-pulse class to the marker
        // element, which the CSS drives via ::after. SART gets its own
        // pulse so we skip it here to avoid double-pulsing.
        if (!isSart) {
            const el = marker.getElement();
            if (el) el.classList.toggle('cpa-pulse', isDangerEff);
        }

        // Vessel staleness. Anything not heard from in >30 s is
        // geometrically stale -- its rendered position is a guess,
        // not a fix. Fade the marker + trail so the helm's eye lands
        // on live targets first. SART pulses regardless (life-safety
        // beacons can drop out briefly and still matter); buddies
        // also keep full opacity because the "where's my friend"
        // workflow tolerates lateness. When a previously-faded target
        // flips to SART/buddy status mid-session we MUST clear the
        // opacity style we wrote earlier, otherwise it stays dim.
        {
            const el = marker.getElement();
            if (el) {
                if (isSart || v.buddy) {
                    if (el.style.opacity !== '') el.style.opacity = '';
                } else {
                    const ageSec = v.ageSec ?? 0;
                    let op;
                    if (ageSec >= 300)      op = '0.25';     // >5 min
                    else if (ageSec >= 30)  op = (1 - 0.65 * (ageSec - 30) / 270).toFixed(2);
                    else                    op = '';         // fresh
                    // Only write when the bucket actually changes; 200+
                    // vessels in a harbour re-writing style every tick
                    // invalidates layout for nothing.
                    if (el.style.opacity !== op) el.style.opacity = op;
                }
            }
        }

        // Name label visible at zoom >= 12. Prefer resolved external name
        // over raw MMSI so the chart looks clean even for unnamed targets.
        // Buddies get a star prefix.
        const resolvedName = v.name || (v.mmsi ? vesselNameCacheGet(v.mmsi) : null);
        const baseName = resolvedName || (v.mmsi ? v.mmsi : null);
        const displayName = baseName ? (v.buddy ? '\u2605 ' + baseName : baseName) : null;
        if (displayName) {
            if (!aisLabels[v.context]) {
                aisLabels[v.context] = L.tooltip({
                    permanent: true, direction: 'right', offset: [12, 0],
                    className: 'ais-label'
                });
                marker.bindTooltip(aisLabels[v.context]);
            }
            aisLabels[v.context].setContent(esc(displayName));
        }

        // Rich popup with vessel details and external lookup links.
        const name = esc(v.name || '');
        const mmsi = v.mmsi || '';
        const callsign = v.callsign ? esc(v.callsign) : '';
        const sog = v.sogMs != null ? (v.sogMs * 1.94384).toFixed(1) : '--';
        const cogDeg = v.cogRad != null ? (v.cogRad * DEG).toFixed(0) : '--';
        const hdgDeg = v.headingRad != null ? (v.headingRad * DEG).toFixed(0) : '--';
        const type = v.shipType ? esc(v.shipType) : '';
        const dist = haversineMeters(selfLat, selfLon, v.lat, v.lon) * NM_PER_METER;
        const brg = bearingDeg(selfLat, selfLon, v.lat, v.lon);

        // Display name preference: SignalK name > external-lookup cache >
        // callsign > MMSI. For unnamed vessels, kick off an external lookup
        // in the background; next update tick will pick up the resolved name.
        let displayTitle;
        const cachedName = mmsi ? vesselNameCacheGet(mmsi) : undefined;
        if (name) {
            displayTitle = name;
        } else if (cachedName) {
            displayTitle = esc(cachedName);
        } else if (callsign) {
            displayTitle = callsign;
        } else if (mmsi) {
            displayTitle = `MMSI ${esc(mmsi)}`;
        } else {
            displayTitle = 'Unknown';
        }
        if (!name && mmsi && !vesselNameCacheHas(mmsi)) {
            resolveVesselName(v.context, mmsi);
        }
        if (v.buddy) displayTitle = '\u2605 ' + displayTitle;

        let cpaHtml = '';
        if (cpaInfo && cpaInfo.tcpa > 0) {
            const cls = isDangerEff ? 'color:#f87171;font-weight:600' : 'opacity:0.8';
            cpaHtml = `<tr><td style="opacity:0.5">CPA</td><td style="${cls}">${cpaInfo.cpa.toFixed(2)} nm in ${cpaInfo.tcpa.toFixed(0)} min</td></tr>`;
        }

        let colregsHtml = '';
        if (v.colregsLabel) {
            const roleHtml = v.colregsRole
                ? ` <span style="color:${v.colregsRole === 'Give way' ? '#fca5a5' : '#86efac'};font-weight:600">${esc(v.colregsRole)}</span>`
                : '';
            colregsHtml = `<tr><td style="opacity:0.5">COLREGS</td><td>${esc(v.colregsLabel)}${roleHtml}</td></tr>`;
        }

        // External lookup links (free, no API key needed). VesselFinder's
        // search page uses ?name= even for MMSI queries.
        const mtUrl = mmsi ? `https://www.marinetraffic.com/en/ais/details/ships/mmsi:${esc(mmsi)}` : '';
        const vfUrl = mmsi ? `https://www.vesselfinder.com/vessels?name=${esc(mmsi)}` : '';

        // Buddy toggle + per-vessel snooze. Inline data attributes so the
        // delegated handler above can route both to Blazor without
        // leaking a callback through string concatenation. Snooze only
        // appears when the vessel is in the warning / danger CPA band
        // -- no point offering to silence a vessel that isn't alarming.
        const buddyLabel = v.buddy ? '\u2605 Remove buddy' : '\u2606 Add buddy';
        const buddyAttrs = `data-ona-buddy="1" data-ctx="${esc(v.context)}" data-mmsi="${esc(mmsi || '')}"`
            + ` data-nm="${esc(v.name || '')}" data-is="${v.buddy ? '1' : '0'}"`;
        const showSnooze = !v.buddy && (isDangerEff || isWarning);
        const snoozeAttrs = `data-ona-snooze="1" data-ctx="${esc(v.context)}" data-nm="${esc(v.name || mmsi || '')}"`;
        const snoozeHtml = showSnooze
            ? `<a href="#" ${snoozeAttrs} style="color:#fbbf24;font-size:11px;text-decoration:none">\u266B Snooze alarm</a>`
            : '';

        let linksHtml = '';
        if (mmsi || showSnooze) {
            const pieces = [];
            if (mmsi) {
                pieces.push(`<a href="${mtUrl}" target="_blank" rel="noopener" style="color:#7dd3fc;font-size:11px;text-decoration:none">MarineTraffic</a>`);
                pieces.push(`<a href="${vfUrl}" target="_blank" rel="noopener" style="color:#7dd3fc;font-size:11px;text-decoration:none">VesselFinder</a>`);
                pieces.push(`<a href="#" ${buddyAttrs} style="color:#facc15;font-size:11px;text-decoration:none">${buddyLabel}</a>`);
            }
            if (snoozeHtml) pieces.push(snoozeHtml);
            linksHtml = `<div style="margin-top:6px;padding-top:6px;border-top:1px solid rgba(255,255,255,0.08);display:flex;gap:10px;flex-wrap:wrap">` +
                pieces.join('') + `</div>`;
        }

        // Country flag from the signalk-flags plugin if installed.
        // Endpoint: /signalk/v2/api/resources/flags/mmsi/{mmsi} returns SVG.
        // Relative URL resolves against the page origin, which IS the
        // SignalK server when OnaPlotter is deployed as a webapp. If the
        // plugin isn't installed the 404 triggers onerror and we hide
        // the img so there's no broken-image glyph. No fallback fetch
        // needed -- country is a nice-to-have, not safety-critical.
        const flagHtml = mmsi
            ? `<img class="ais-popup-flag" src="/signalk/v2/api/resources/flags/mmsi/${esc(mmsi)}" alt="" onerror="this.style.display='none'">`
            : '';

        const popupHtml =
            `<div class="ais-popup-content">` +
            `<div class="ais-popup-title">${flagHtml}${displayTitle}</div>` +
            (type ? `<div class="ais-popup-type">${type}</div>` : '') +
            `<table class="ais-popup-table">` +
              (mmsi ? `<tr><td>MMSI</td><td>${esc(mmsi)}</td></tr>` : '') +
              (callsign ? `<tr><td>Call</td><td>${callsign}</td></tr>` : '') +
              `<tr><td>SOG</td><td>${sog} kn</td></tr>` +
              `<tr><td>COG</td><td>${cogDeg}&deg;</td></tr>` +
              `<tr><td>HDG</td><td>${hdgDeg}&deg;</td></tr>` +
              `<tr><td>Dist</td><td>${dist.toFixed(2)} nm</td></tr>` +
              `<tr><td>BRG</td><td>${brg.toFixed(0)}&deg;</td></tr>` +
              cpaHtml +
              colregsHtml +
            `</table>` +
            linksHtml +
            `</div>`;

        // If the popup is already bound (common - repeated update ticks),
        // use setPopupContent so an open popup updates live (the buddy
        // toggle relies on this to show the new Remove/Add label without
        // a close-reopen round-trip). Otherwise bindPopup for the first time.
        if (marker.getPopup()) {
            marker.setPopupContent(popupHtml);
        } else {
            marker.bindPopup(popupHtml,
                { closeButton: false, maxWidth: 280, className: 'ais-popup' });
        }

        // Trail: last AIS_TRAIL_SECONDS of positions, drawn as a fading line.
        // We only push when the position actually changes to avoid empty ticks.
        updateAisTrail(v.context, v.lat, v.lon);

        // Course vector.
        const end = vectorEnd(v.lat, v.lon, v.cogRad, v.sogMs);
        if (end) {
            let vec = aisVectors[v.context];
            if (!vec) {
                vec = L.polyline([[v.lat, v.lon], end], {
                    color, weight: 1.5, dashArray: '6,4'
                }).addTo(map);
                aisVectors[v.context] = vec;
            } else {
                vec.setLatLngs([[v.lat, v.lon], end]);
                vec.setStyle({ color });
            }
        }

        // Crossing-situation lines: draw from each vessel's current position
        // to its predicted CPA point, plus a label with CPA / TCPA at the
        // target's CPA dot. Rendered for danger (red) and warning (yellow).
        // Buddies never render these - they're exempt from the alarm pipeline
        // and the red lines would be misleading.
        if ((isDangerEff || isWarning) && cpaInfo && cpaInfo.tcpa > 0) {
            const tcpaSec = cpaInfo.tcpa * 60;
            const ownCpa = destPoint(selfLat, selfLon, selfCogRad, selfSogMs * tcpaSec);
            const tgtCpa = destPoint(v.lat, v.lon, v.cogRad, v.sogMs * tcpaSec);
            const lineColor = isDangerEff ? '#ef4444' : '#f59e0b';

            updateCpaLine(aisCpaOwnLines, v.context, [selfLat, selfLon], ownCpa, lineColor);
            updateCpaLine(aisCpaTgtLines, v.context, [v.lat, v.lon], tgtCpa, lineColor);

            const midLat = (ownCpa[0] + tgtCpa[0]) / 2;
            const midLon = (ownCpa[1] + tgtCpa[1]) / 2;
            // Label now leads with the target name (or MMSI fallback) so a
            // sailor glancing at the chart knows WHICH vessel is on a
            // collision track without having to click the marker. Format:
            //   MV Aurora
            //   0.42 nm · T-5m
            const cpaName = baseName || 'Unknown';
            const labelText = `<strong>${esc(cpaName)}</strong><br>${cpaInfo.cpa.toFixed(2)} nm · T-${cpaInfo.tcpa.toFixed(0)}m`;
            let lbl = aisCpaLabels[v.context];
            if (!lbl) {
                lbl = L.tooltip({
                    permanent: true, direction: 'center',
                    className: `cpa-label ${isDangerEff ? 'cpa-danger' : 'cpa-warn'}`
                }).setLatLng([midLat, midLon]).setContent(labelText).addTo(map);
                aisCpaLabels[v.context] = lbl;
            } else {
                lbl.setLatLng([midLat, midLon]);
                lbl.setContent(labelText);
                const el = lbl.getElement();
                if (el) {
                    el.classList.toggle('cpa-danger', isDangerEff);
                    el.classList.toggle('cpa-warn', !isDangerEff);
                }
            }
        } else {
            removeCpaOverlay(v.context);
        }
    }

    // Remove stale markers.
    for (const ctx of Object.keys(aisMarkers)) {
        if (!seen.has(ctx)) {
            map.removeLayer(aisMarkers[ctx]);
            delete aisMarkers[ctx];
            if (aisVectors[ctx]) { map.removeLayer(aisVectors[ctx]); delete aisVectors[ctx]; }
            delete aisLabels[ctx];
            removeCpaOverlay(ctx);
            removeAisTrail(ctx);
        }
    }
}

/**
 * Vessel-name enrichment is disabled. The previous implementation used
 * api.allorigins.win as a CORS proxy to scrape vesselfinder.com, but that
 * proxy itself stopped sending CORS headers and now floods the console.
 * The SignalK server already receives AIS message type 5 (static data)
 * for named vessels within minutes of first sighting, so the common
 * scenario is "wait a bit and the name shows up". A proper long-term
 * home for this lookup is a SignalK server-side plugin.
 *
 * The function is kept as a no-op so call sites remain; the per-MMSI
 * cache is still honored for any externally injected values.
 */
async function resolveVesselName(_context, _mmsi) {
    return null;
}

function updateAisTrail(ctx, lat, lon) {
    const now = Date.now();
    const hist = aisTrailHistory[ctx] ||= [];
    const last = hist[hist.length - 1];
    if (!last || last.lat !== lat || last.lon !== lon) hist.push({ lat, lon, t: now });

    // Drop points older than the trail window.
    const cutoff = now - AIS_TRAIL_SECONDS * 1000;
    while (hist.length > 0 && hist[0].t < cutoff) hist.shift();

    if (hist.length < 2) return;
    const coords = hist.map(p => [p.lat, p.lon]);
    let line = aisTrailLines[ctx];
    if (!line) {
        line = L.polyline(coords, {
            color: '#94a3b8', weight: 1.2, opacity: 0.45, interactive: false
        }).addTo(map);
        aisTrailLines[ctx] = line;
    } else {
        line.setLatLngs(coords);
    }
}

function removeAisTrail(ctx) {
    if (aisTrailLines[ctx]) { map.removeLayer(aisTrailLines[ctx]); delete aisTrailLines[ctx]; }
    delete aisTrailHistory[ctx];
}

function updateCpaLine(store, ctx, from, to, color) {
    let line = store[ctx];
    if (!line) {
        line = L.polyline([from, to], {
            color, weight: 2, dashArray: '4,4', opacity: 0.9
        }).addTo(map);
        store[ctx] = line;
    } else {
        line.setLatLngs([from, to]);
        line.setStyle({ color });
    }
}

function removeCpaOverlay(ctx) {
    if (aisCpaOwnLines[ctx]) { map.removeLayer(aisCpaOwnLines[ctx]); delete aisCpaOwnLines[ctx]; }
    if (aisCpaTgtLines[ctx]) { map.removeLayer(aisCpaTgtLines[ctx]); delete aisCpaTgtLines[ctx]; }
    if (aisCpaLabels[ctx]) { map.removeLayer(aisCpaLabels[ctx]); delete aisCpaLabels[ctx]; }
}

/**
 * Updates the collision-avoidance thresholds used to colour AIS targets and
 * draw crossing-situation lines. Also resizes the guard zone ring.
 * @param radiusNm    CPA threshold (nautical miles)
 * @param lookaheadMin  TCPA threshold (minutes)
 */
/** Pans the map to an AIS vessel and opens its popup. */
export function focusVessel(context) {
    const marker = aisMarkers[context];
    if (!marker || !map) return;
    const ll = marker.getLatLng();
    map.panTo(ll, { animate: true });
    marker.openPopup();
}

/**
 * Updates collision thresholds used to colour AIS targets and draw the
 * crossing-situation lines. A target whose CPA/TCPA is inside the raw
 * guard zone gets a red line; a target inside guardZone*warningFactor
 * gets amber. Pass warningFactor <= 1 to disable the amber band.
 */
export function setGuardZone(radiusNm, lookaheadMin, warningFactor) {
    guardZoneRadiusNm = radiusNm;
    guardZoneLookaheadMin = lookaheadMin;
    if (typeof warningFactor === 'number' && warningFactor > 1) {
        guardZoneWarningFactor = warningFactor;
    }
    drawGuardZone();
}

function drawGuardZone() {
    if (!map) return;
    // Disabled (radius <= 0) - remove the ring entirely instead of shrinking
    // it to a zero-radius invisible point we would still reposition every tick.
    if (guardZoneRadiusNm <= 0) {
        if (guardZoneRing) { map.removeLayer(guardZoneRing); guardZoneRing = null; }
        return;
    }
    const radiusM = guardZoneRadiusNm * 1852;
    if (!guardZoneRing) {
        guardZoneRing = L.circle([selfLat, selfLon], {
            radius: radiusM,
            color: '#f59e0b',
            weight: 1,
            opacity: 0.5,
            fillColor: '#f59e0b',
            fillOpacity: 0.04,
            interactive: false
        }).addTo(map);
    } else {
        guardZoneRing.setLatLng([selfLat, selfLon]);
        guardZoneRing.setRadius(radiusM);
    }
}

// --- Bearing/Distance ---

function drawBearingLine(lat, lon) {
    if (!map || !boatMarker) return;
    clearBearingLine();

    const dist = haversineMeters(selfLat, selfLon, lat, lon) * NM_PER_METER;
    const brg = bearingDeg(selfLat, selfLon, lat, lon);

    bearingLine = L.polyline([[selfLat, selfLon], [lat, lon]], {
        color: '#e2e8f0', weight: 1.5, dashArray: '8,6', opacity: 0.7
    }).addTo(map);

    bearingLabel = L.tooltip({ permanent: true, direction: 'center', className: 'bearing-tooltip' })
        .setLatLng([(selfLat + lat)/2, (selfLon + lon)/2])
        .setContent(`${brg.toFixed(0)}&deg; / ${dist.toFixed(2)} nm`)
        .addTo(map);
}

function clearBearingLine() {
    if (bearingLine) { map.removeLayer(bearingLine); bearingLine = null; }
    if (bearingLabel) { map.removeLayer(bearingLabel); bearingLabel = null; }
}

// --- Persistent measurement tool ---
// Multi-segment ruler: clicks drop measurement points, each segment
// is labelled with bearing + distance, running total shown on the
// last point. Intentionally NOT anchored to own-boat (the dblclick
// helper above covers that) -- this is for chart planning, e.g.
// summing the distance of a route before you create it.
let measureActive = false;
let measurePoints = [];     // [[lat, lon], ...]
const measureLayers = [];    // parallel array of L.Polyline / L.Marker

export function setMeasureMode(active) {
    measureActive = !!active;
    if (!measureActive) clearMeasure();
    if (map) {
        // Visual hint: crosshair cursor when in measurement mode.
        map.getContainer().style.cursor = measureActive ? 'crosshair' : '';
    }
}

export function clearMeasure() {
    for (const layer of measureLayers) {
        if (map) map.removeLayer(layer);
    }
    measureLayers.length = 0;
    measurePoints = [];
}

function addMeasurePoint(lat, lon) {
    if (!map) return;
    measurePoints.push([lat, lon]);

    // A big enough dot to tap-to-delete later; also a waypoint-style visual.
    const dot = L.circleMarker([lat, lon], {
        radius: 5, color: '#e9c46a', fillColor: '#e9c46a',
        fillOpacity: 1, weight: 2
    }).addTo(map);
    measureLayers.push(dot);

    if (measurePoints.length >= 2) {
        const a = measurePoints[measurePoints.length - 2];
        const b = measurePoints[measurePoints.length - 1];
        const segDist = haversineMeters(a[0], a[1], b[0], b[1]) * NM_PER_METER;
        const segBrg = bearingDeg(a[0], a[1], b[0], b[1]);
        const seg = L.polyline([a, b], {
            color: '#e9c46a', weight: 2, dashArray: '6,4', opacity: 0.85
        }).addTo(map);
        measureLayers.push(seg);

        const totalDist = measurePoints.slice(1).reduce((acc, p, i) => {
            return acc + haversineMeters(measurePoints[i][0], measurePoints[i][1], p[0], p[1]);
        }, 0) * NM_PER_METER;

        const tooltip = L.tooltip({
            permanent: true, direction: 'right', offset: [8, 0],
            className: 'measure-tooltip'
        })
            .setLatLng(b)
            .setContent(`${segBrg.toFixed(0)}&deg; / ${segDist.toFixed(2)} nm<br/>total ${totalDist.toFixed(2)} nm`)
            .addTo(map);
        measureLayers.push(tooltip);
    }
}

// --- MOB ---

export function setMob(lat, lon) {
    clearMob();
    mobMarker = L.marker([lat, lon], { icon: mobIcon, zIndexOffset: 2000 }).addTo(map);
    mobCircle = L.circle([lat, lon], { radius: 50, color: '#ef4444', fillColor: '#ef4444',
        fillOpacity: 0.15, weight: 2 }).addTo(map);
    mobLine = L.polyline([[selfLat, selfLon], [lat, lon]], {
        color: '#ef4444', weight: 2, dashArray: '4,4'
    }).addTo(map);
    mobLabel = L.tooltip({ permanent: true, direction: 'center', className: 'mob-tooltip' })
        .setLatLng([(selfLat + lat)/2, (selfLon + lon)/2])
        .setContent('MOB')
        .addTo(map);
}

export function clearMob() {
    if (mobMarker) { map.removeLayer(mobMarker); mobMarker = null; }
    if (mobCircle) { map.removeLayer(mobCircle); mobCircle = null; }
    if (mobLine) { map.removeLayer(mobLine); mobLine = null; }
    if (mobLabel) { map.removeLayer(mobLabel); mobLabel = null; }
}

// --- Anchor Watch ---

export function setAnchor(lat, lon, radiusM) {
    clearAnchor();
    anchorMarker = L.circleMarker([lat, lon], {
        radius: 5, color: '#22c55e', fillColor: '#22c55e', fillOpacity: 1
    }).addTo(map);
    anchorCircle = L.circle([lat, lon], {
        radius: radiusM, color: '#22c55e', fillColor: '#22c55e',
        fillOpacity: 0.06, weight: 2, dashArray: '6,4'
    }).addTo(map);
    // Seed the trail with the current boat position so the first segment
    // renders without waiting for ANCHOR_TRAIL_SAMPLE_MS.
    if (selfLat && selfLon) anchorTrail.push({ lat: selfLat, lon: selfLon, t: Date.now() });
}

export function clearAnchor() {
    if (anchorMarker) { map.removeLayer(anchorMarker); anchorMarker = null; }
    if (anchorCircle) { map.removeLayer(anchorCircle); anchorCircle = null; }
    if (anchorTrailLayer) { map.removeLayer(anchorTrailLayer); anchorTrailLayer = null; }
    anchorTrail.length = 0;
}

export function updateAnchorRadius(radiusM) {
    if (anchorCircle) anchorCircle.setRadius(radiusM);
}

function updateAnchorTrail(lat, lon) {
    if (!anchorMarker) {
        // Anchor not set: tear down any residual trail.
        if (anchorTrailLayer) { map.removeLayer(anchorTrailLayer); anchorTrailLayer = null; }
        anchorTrail.length = 0;
        return;
    }

    const now = Date.now();
    const last = anchorTrail[anchorTrail.length - 1];
    if (!last || now - last.t >= ANCHOR_TRAIL_SAMPLE_MS) {
        anchorTrail.push({ lat, lon, t: now });
    } else {
        // Within the sample window - update the latest point so the trail
        // head follows the boat smoothly.
        last.lat = lat; last.lon = lon;
    }

    // Drop points outside the rolling window.
    const cutoff = now - ANCHOR_TRAIL_MINUTES * 60_000;
    while (anchorTrail.length > 0 && anchorTrail[0].t < cutoff) anchorTrail.shift();

    if (anchorTrail.length < 2) return;
    const coords = anchorTrail.map(p => [p.lat, p.lon]);
    if (!anchorTrailLayer) {
        anchorTrailLayer = L.polyline(coords, {
            color: '#22c55e', weight: 2, opacity: 0.55,
            dashArray: '2,4', interactive: false
        }).addTo(map);
    } else {
        anchorTrailLayer.setLatLngs(coords);
    }
}

// --- Night Mode ---

export function setNightMode(enabled) {
    nightMode = enabled;
    if (!map) return;
    const pane = map.getPane('tilePane');
    if (pane) {
        pane.style.filter = enabled
            ? 'brightness(0.35) sepia(1) hue-rotate(-30deg) saturate(3)'
            : '';
    }
}

// --- Chart Layers ---

// Add a chart tile layer from SignalK.
// bounds is [west, south, east, north] or null.
// Per-chart native maxZoom, kept so recomputeChartOverzoom() can tell
// the top-native chart from the rest after every add/remove.
const chartNativeMax = new Map();

export function addChartLayer(id, tileUrl, minZoom, maxZoom, opacity, bounds) {
    if (!map || chartLayers.has(id)) return false;
    const native = maxZoom || 18;
    chartNativeMax.set(id, native);
    // maxZoom is set provisionally to the native cap; recomputeChartOverzoom
    // below raises it only for the top-native chart. That way a detailed
    // harbour chart (native 18) kicks in past a wide-area chart (native 12)
    // instead of the wide-area chart blurring over everything at zoom 20.
    const opts = {
        minZoom: minZoom || 1,
        maxNativeZoom: native,
        maxZoom: native,
        opacity: opacity || 0.8,
        // keepBuffer + updateWhenIdle match base layers; don't re-fetch
        // chart tiles when the user zig-zags back into territory they
        // just panned away from.
        keepBuffer: 4,
        updateWhenIdle: false,
        crossOrigin: 'anonymous',
        attribution: '',
        errorTileUrl: ''  // Suppress broken tile images for out-of-bounds requests.
    };
    // Constrain tile requests to the chart's coverage area.
    if (bounds && bounds.length === 4) {
        opts.bounds = L.latLngBounds(
            [bounds[1], bounds[0]],  // SW: [south, west]
            [bounds[3], bounds[2]]   // NE: [north, east]
        );
    }
    const layer = L.tileLayer(tileUrl, opts);
    layer.addTo(map);
    layer.setZIndex(50);
    chartLayers.set(id, layer);
    recomputeChartOverzoom();
    return true;
}

export function removeChartLayer(id) {
    chartLayers.remove(id);
    chartNativeMax.delete(id);
    recomputeChartOverzoom();
}

// Apply a user-chosen chart draw order. `orderedIds` is bottom-to-top,
// matching IAppSettings.ChartOrder convention. Each enabled layer's
// z-index is updated so Leaflet paints them in the requested order,
// regardless of .addTo insertion order. Ids not currently enabled are
// skipped silently -- they'll be positioned when they're re-enabled.
export function setChartLayerOrder(orderedIds) {
    if (!map || !orderedIds) return;
    const base = 50;
    for (let i = 0; i < orderedIds.length; i++) {
        const layer = chartLayers.get(orderedIds[i]);
        if (layer) layer.setZIndex(base + i);
    }
    recomputeChartOverzoom();
}

// Smart overzoom: only the chart with the highest native max gets the
// "stretch past its native limit" treatment. Lower-native charts keep
// their maxZoom equal to their native max, so they naturally hide past
// their native instead of pixel-stretching over a more-detailed chart.
// If two charts share the top native, both overzoom -- harmless since
// the later-added one draws on top anyway.
function recomputeChartOverzoom() {
    if (chartLayers.map.size === 0) {
        if (zoomBadge) zoomBadge.update();
        return;
    }
    let topNative = 0;
    for (const native of chartNativeMax.values()) {
        if (native > topNative) topNative = native;
    }
    for (const [id, layer] of chartLayers.map.entries()) {
        const native = chartNativeMax.get(id) || 18;
        const effMax = native >= topNative ? Math.max(native + 4, 22) : native;
        if (layer.options.maxZoom !== effMax) {
            layer.options.maxZoom = effMax;
            // Leaflet reads options.maxZoom on tile-visibility checks.
            // redraw() forces it to recompute which tiles to paint at
            // the current map zoom, which is what we actually want.
            layer.redraw();
        }
    }
    if (zoomBadge) zoomBadge.update();
}

// --- Routes ---

// Route polyline colour. Warm amber contrasts cleanly with OSM blue
// water and doesn't clash with AIS ship colours (same family).
const ROUTE_COLOR = '#e09f3e';

// Add a route as a polyline. coords is [[lat, lon], ...].
export function addRoute(id, name, coords) {
    if (!map || routeLayers.has(id)) return;
    const line = L.polyline(coords, {
        color: ROUTE_COLOR, weight: 2.5, opacity: 0.8, dashArray: '8,6'
    }).addTo(map);
    // Waypoint dots at each coordinate.
    const group = L.layerGroup([line]).addTo(map);
    for (let i = 0; i < coords.length; i++) {
        const dot = L.circleMarker(coords[i], {
            radius: 4, color: ROUTE_COLOR, fillColor: ROUTE_COLOR, fillOpacity: 1, weight: 1
        });
        dot.bindTooltip(name ? `${name} [${i + 1}]` : `WPT ${i + 1}`, { className: 'bearing-tooltip' });
        dot.addTo(group);
    }
    routeLayers.set(id, group);
}

export function removeRoute(id) { routeLayers.remove(id); }

// --- Server Track ---

// Show the server-side historical track. coords is [[lat, lon], ...].
export function setServerTrack(coords) {
    clearServerTrack();
    if (!map || !coords || coords.length === 0) return;
    serverTrackLayer = L.polyline(coords, {
        color: '#94a3b8', weight: 2, opacity: 0.5
    }).addTo(map);
}

export function clearServerTrack() {
    if (serverTrackLayer && map) { map.removeLayer(serverTrackLayer); serverTrackLayer = null; }
}

// --- Weather-routing overlay ---
//
// Drawn on top of the chart as a thicker amber polyline with a dashed
// outline - visually distinct from saved routes (solid amber), the
// server track (grey), and own-track (speed-coloured). Clears any
// previous weather route first so re-running replaces, not stacks.
let weatherRouteLayer = null;

// windSamples (optional): [[lat, lon, dirFromDeg, speedKn], ...] -- small
// amber arrows drawn along the route so the user can eyeball where the
// wind swings without leaving the chart. The `dirFromDeg` convention is
// meteorological (degrees FROM which the wind blows); we rotate the arrow
// to point WITH the wind (i.e. rotate by dirFromDeg + 180).
export function setWeatherRoute(coords, windSamples) {
    clearWeatherRoute();
    if (!map || !coords || coords.length < 2) return;
    const layers = [
        L.polyline(coords, { color: '#000', weight: 5, opacity: 0.35 }),
        L.polyline(coords, { color: '#e9c46a', weight: 3, opacity: 0.95, dashArray: '10,6' }),
        L.circleMarker(coords[0],        { radius: 4, color: '#e9c46a', fillColor: '#e9c46a', fillOpacity: 1 }),
        L.circleMarker(coords[coords.length - 1], { radius: 5, color: '#e9c46a', fillColor: '#fff', fillOpacity: 1, weight: 2 }),
    ];
    if (Array.isArray(windSamples)) {
        for (const s of windSamples) {
            if (!Array.isArray(s) || s.length < 4) continue;
            const [lat, lon, dirFromDeg, spdKn] = s;
            layers.push(L.marker([lat, lon], { icon: makeWindArrowIcon(dirFromDeg, spdKn), interactive: false }));
        }
    }
    weatherRouteLayer = L.layerGroup(layers).addTo(map);
}

// Small wind-arrow divIcon. Arrow length scales mildly with speed so a
// 20kn gust reads bigger than a 5kn zephyr; clamped so it doesn't take
// over the viewport. Rotation uses wind-TO direction (dirFrom + 180).
function makeWindArrowIcon(dirFromDeg, spdKn) {
    const size = 36;
    const dirTo = ((dirFromDeg || 0) + 180) % 360;
    const spd = Math.max(0, Math.min(40, spdKn || 0));
    const len = 8 + spd * 0.5;   // 8px at 0kn, 28px at 40kn
    const half = size / 2;
    const svg = `
        <svg width="${size}" height="${size}" viewBox="-${half} -${half} ${size} ${size}"
             style="transform: rotate(${dirTo}deg); overflow: visible;">
            <line x1="0" y1="${-len}" x2="0" y2="${len * 0.3}"
                  stroke="#000" stroke-width="3" stroke-linecap="round" opacity="0.35"/>
            <line x1="0" y1="${-len}" x2="0" y2="${len * 0.3}"
                  stroke="#e9c46a" stroke-width="1.6" stroke-linecap="round"/>
            <polygon points="0,${-len - 3} 3.5,${-len + 4} 0,${-len + 1.5} -3.5,${-len + 4}"
                     fill="#e9c46a" stroke="#000" stroke-width="0.5" opacity="0.95"/>
            <text x="0" y="${len + 7}" fill="#e9c46a" stroke="#000" stroke-width="0.4"
                  text-anchor="middle" font-size="9" font-weight="600"
                  style="paint-order: stroke; transform: rotate(${-dirTo}deg);
                         transform-box: fill-box; transform-origin: center;">
                ${Math.round(spd)}
            </text>
        </svg>`;
    return L.divIcon({
        className: 'wind-arrow-icon',
        html: svg,
        iconSize: [size, size],
        iconAnchor: [half, half],
    });
}

export function clearWeatherRoute() {
    if (weatherRouteLayer && map) { map.removeLayer(weatherRouteLayer); weatherRouteLayer = null; }
}

// --- Active Route Navigation ---

const activeWpIcon = L.divIcon({
    className: 'active-wp-icon',
    html: '<div class="active-wp-pulse"></div>',
    iconSize: [24, 24],
    iconAnchor: [12, 12]
});

function findClosestWaypointIndex(coords, lat, lon) {
    let minDist = Infinity;
    let idx = 0;
    for (let i = 0; i < coords.length; i++) {
        const d = haversineMeters(lat, lon, coords[i][0], coords[i][1]);
        if (d < minDist) { minDist = d; idx = i; }
    }
    return idx;
}

// Draw the full active route polyline with numbered waypoint markers.
// Highlights the next waypoint and dims passed ones.
export function setActiveRoute(coords, nextWpLat, nextWpLon) {
    clearActiveRoute();
    if (!map || !coords || coords.length < 2) return;

    activeRouteCoords = coords;
    const wpIdx = findClosestWaypointIndex(coords, nextWpLat, nextWpLon);
    activeRouteLayer = L.layerGroup().addTo(map);

    // Full route polyline.
    L.polyline(coords, {
        color: '#06b6d4', weight: 3, opacity: 0.8
    }).addTo(activeRouteLayer);

    // Waypoint markers.
    for (let i = 0; i < coords.length; i++) {
        const isPassed = i < wpIdx;
        const isNext = i === wpIdx;
        const radius = isNext ? 0 : (isPassed ? 3 : 5);

        if (!isNext) {
            const dot = L.circleMarker(coords[i], {
                radius,
                color: '#06b6d4',
                fillColor: isPassed ? '#64748b' : '#06b6d4',
                fillOpacity: isPassed ? 0.35 : 1,
                weight: isPassed ? 1 : 1.5,
                opacity: isPassed ? 0.35 : 1
            });
            dot.bindTooltip(`${i + 1}`, {
                permanent: false,
                direction: 'right',
                offset: [8, 0],
                className: 'route-wp-tooltip'
            });
            dot.addTo(activeRouteLayer);
        }
    }

    // Pulsing marker at the next waypoint.
    nextWpMarker = L.marker(coords[wpIdx], {
        icon: activeWpIcon,
        zIndexOffset: 900
    }).addTo(activeRouteLayer);
    nextWpMarker.bindTooltip(`WP ${wpIdx + 1}`, {
        permanent: true,
        direction: 'right',
        offset: [14, 0],
        className: 'route-wp-tooltip'
    });
}

export function clearActiveRoute() {
    if (activeRouteLayer && map) { map.removeLayer(activeRouteLayer); }
    activeRouteLayer = null;
    activeRouteCoords = null;
    nextWpMarker = null;
}

// Update only the active waypoint highlight (lightweight, no full redraw).
export function updateActiveWaypoint(nextWpLat, nextWpLon) {
    if (!activeRouteCoords || !map) return;
    // Full redraw is simplest and still fast for typical route sizes (<50 WPs).
    setActiveRoute(activeRouteCoords, nextWpLat, nextWpLon);
}

// Draw/update course line: leg line, bearing line, XTE tick.
// Called on every position update when an active course exists.
export function setCourseLine(boatLat, boatLon, wpLat, wpLon, prevLat, prevLon, xteMeters) {
    if (!map) return;

    // Leg line: previous WP to next WP.
    if (prevLat != null && prevLon != null) {
        const legCoords = [[prevLat, prevLon], [wpLat, wpLon]];
        if (courseLineLeg) {
            courseLineLeg.setLatLngs(legCoords);
        } else {
            courseLineLeg = L.polyline(legCoords, {
                color: '#fff', weight: 2, opacity: 0.25, dashArray: '10,8'
            }).addTo(map);
        }
    } else if (courseLineLeg) {
        map.removeLayer(courseLineLeg);
        courseLineLeg = null;
    }

    // Bearing line: boat to next WP.
    const brgCoords = [[boatLat, boatLon], [wpLat, wpLon]];
    if (courseLineBearing) {
        courseLineBearing.setLatLngs(brgCoords);
    } else {
        courseLineBearing = L.polyline(brgCoords, {
            color: '#06b6d4', weight: 2, opacity: 0.7, dashArray: '6,4'
        }).addTo(map);
    }

    // XTE perpendicular tick at boat position.
    if (xteMeters != null && prevLat != null && prevLon != null) {
        const absXte = Math.abs(xteMeters);
        const xteColor = absXte < 50 ? '#22c55e' : absXte < 200 ? '#f59e0b' : '#ef4444';
        // Perpendicular to the leg bearing.
        const legBrg = bearingDeg(prevLat, prevLon, wpLat, wpLon) * RAD;
        const perpBrg = xteMeters > 0 ? legBrg + Math.PI / 2 : legBrg - Math.PI / 2;
        // Visual length: actual XTE capped at 200m for display.
        const tickLen = Math.min(absXte, 200);
        const tickEnd = destPoint(boatLat, boatLon, perpBrg, tickLen);
        const xteCoords = [[boatLat, boatLon], tickEnd];

        if (courseLineXte) {
            courseLineXte.setLatLngs(xteCoords);
            courseLineXte.setStyle({ color: xteColor });
        } else {
            courseLineXte = L.polyline(xteCoords, {
                color: xteColor, weight: 3, opacity: 0.9
            }).addTo(map);
        }
    } else if (courseLineXte) {
        map.removeLayer(courseLineXte);
        courseLineXte = null;
    }
}

export function clearCourseLine() {
    if (courseLineLeg && map) { map.removeLayer(courseLineLeg); courseLineLeg = null; }
    if (courseLineBearing && map) { map.removeLayer(courseLineBearing); courseLineBearing = null; }
    if (courseLineXte && map) { map.removeLayer(courseLineXte); courseLineXte = null; }
}

// --- Route Editing ---

let routeEditMode = false;
let routeEditLayer = null;
let routeEditCoords = [];
let routeEditMarkers = [];
let routeEditLine = null;

function makeEditWpIcon(num) {
    return L.divIcon({
        className: 'edit-wp-icon',
        html: `<div class="edit-wp-circle">${num}</div>`,
        iconSize: [24, 24],
        iconAnchor: [12, 12]
    });
}

function redrawEditLine() {
    if (!routeEditLine && routeEditCoords.length >= 2) {
        routeEditLine = L.polyline(routeEditCoords, {
            color: '#a78bfa', weight: 2.5, opacity: 0.8, dashArray: '8,6'
        }).addTo(routeEditLayer);
        // Click on the dashed line between two waypoints inserts a new
        // waypoint at the click point, between those two. stopPropagation
        // prevents the map-level handler (which appends at the end) from
        // also firing. Wider hit polyline underneath gives a chunkier
        // tap target without thickening the visible line.
        routeEditLine.on('click', (e) => {
            L.DomEvent.stopPropagation(e);
            insertEditVertexOnSegment(e.latlng);
        });
    } else if (routeEditLine) {
        routeEditLine.setLatLngs(routeEditCoords);
    }
}

// Pick the segment closest to `ll` (pixel distance at current zoom, so
// "close" matches what the user sees), splice the click point in as a
// new vertex, rebuild numbered markers.
function insertEditVertexOnSegment(ll) {
    if (routeEditCoords.length < 2 || !map) return;
    const p = map.latLngToLayerPoint(ll);
    let bestIdx = 0;
    let bestDist = Infinity;
    for (let i = 0; i < routeEditCoords.length - 1; i++) {
        const a = map.latLngToLayerPoint(L.latLng(routeEditCoords[i][0],     routeEditCoords[i][1]));
        const b = map.latLngToLayerPoint(L.latLng(routeEditCoords[i + 1][0], routeEditCoords[i + 1][1]));
        const d = pointToSegmentPixels(p, a, b);
        if (d < bestDist) { bestDist = d; bestIdx = i; }
    }
    // Insert at bestIdx + 1 so the order becomes ... prev, new, next ...
    routeEditCoords.splice(bestIdx + 1, 0, [ll.lat, ll.lng]);
    rebuildRouteEditMarkers();
}

// Euclidean pixel distance from point p to segment ab.
function pointToSegmentPixels(p, a, b) {
    const dx = b.x - a.x, dy = b.y - a.y;
    const len2 = dx * dx + dy * dy;
    if (len2 === 0) return Math.hypot(p.x - a.x, p.y - a.y);
    let t = ((p.x - a.x) * dx + (p.y - a.y) * dy) / len2;
    t = Math.max(0, Math.min(1, t));
    const cx = a.x + t * dx, cy = a.y + t * dy;
    return Math.hypot(p.x - cx, p.y - cy);
}

function rebuildRouteEditMarkers() {
    if (!routeEditLayer) return;
    for (const m of routeEditMarkers) routeEditLayer.removeLayer(m);
    routeEditMarkers = [];
    for (let i = 0; i < routeEditCoords.length; i++) {
        const [lat, lon] = routeEditCoords[i];
        const marker = L.marker([lat, lon], {
            icon: makeEditWpIcon(i + 1),
            draggable: true, zIndexOffset: 800
        }).addTo(routeEditLayer);
        bindEditMarker(marker, i);
        routeEditMarkers.push(marker);
    }
    redrawEditLine();
}

// Attach drag handlers with a "ghost" visual: during drag we leave the
// original position visible as a hollow ghost marker and draw a dashed
// rubber-band line from it to the live cursor, with a tooltip showing the
// delta distance. This matches what Axiom/Aqua Map do when repositioning
// a waypoint -- the sailor always sees "how far from where it was".
function bindEditMarker(marker, idx) {
    let ghostLine = null;
    let ghostMarker = null;
    let origLL = null;

    marker.on('dragstart', (e) => {
        origLL = e.target.getLatLng();
        ghostMarker = L.marker(origLL, {
            icon: L.divIcon({
                className: 'edit-wp-ghost-icon',
                html: '<div class="edit-wp-ghost-circle"></div>',
                iconSize: [24, 24],
                iconAnchor: [12, 12]
            }),
            interactive: false,
            keyboard: false,
            zIndexOffset: 500
        }).addTo(routeEditLayer);
        ghostLine = L.polyline([origLL, origLL], {
            color: '#a78bfa', weight: 1.5, opacity: 0.7, dashArray: '3,4',
            interactive: false
        }).addTo(routeEditLayer);
        // Bind once; setTooltipContent on each drag event is cheaper than
        // rebinding a fresh tooltip at ~60 Hz during a long drag. We
        // reposition explicitly via setLatLng, so no `sticky` needed.
        ghostLine.bindTooltip('\u0394 0 m', {
            permanent: true, direction: 'center',
            className: 'measure-tooltip'
        }).openTooltip(origLL);
    });

    marker.on('drag', (e) => {
        const ll = e.target.getLatLng();
        routeEditCoords[idx] = [ll.lat, ll.lng];
        redrawEditLine();
        if (ghostLine && origLL) {
            ghostLine.setLatLngs([origLL, ll]);
            const dm = haversineMeters(origLL.lat, origLL.lng, ll.lat, ll.lng);
            const label = dm < 1000 ? `\u0394 ${dm.toFixed(0)} m` : `\u0394 ${(dm * NM_PER_METER).toFixed(2)} nm`;
            ghostLine.setTooltipContent(label);
            const tt = ghostLine.getTooltip();
            if (tt) tt.setLatLng(ll);
        }
    });

    marker.on('dragend', () => {
        if (ghostMarker && routeEditLayer) routeEditLayer.removeLayer(ghostMarker);
        if (ghostLine && routeEditLayer) routeEditLayer.removeLayer(ghostLine);
        ghostMarker = null;
        ghostLine = null;
        origLL = null;
    });
}

function addEditWaypoint(lat, lon) {
    const idx = routeEditCoords.length;
    routeEditCoords.push([lat, lon]);

    const marker = L.marker([lat, lon], {
        icon: makeEditWpIcon(idx + 1),
        draggable: true,
        zIndexOffset: 800
    }).addTo(routeEditLayer);

    bindEditMarker(marker, idx);

    routeEditMarkers.push(marker);
    redrawEditLine();
}

export function startRouteEdit() {
    stopRouteEdit();
    routeEditMode = true;
    routeEditLayer = L.layerGroup().addTo(map);
}

export function stopRouteEdit() {
    routeEditMode = false;
    if (routeEditLayer && map) map.removeLayer(routeEditLayer);
    routeEditLayer = null;
    routeEditCoords = [];
    routeEditMarkers = [];
    routeEditLine = null;
}

export function getEditRouteCoords() {
    return routeEditCoords;
}

export function undoLastEditWaypoint() {
    if (routeEditCoords.length === 0) return;
    routeEditCoords.pop();
    const last = routeEditMarkers.pop();
    if (last && routeEditLayer) routeEditLayer.removeLayer(last);
    redrawEditLine();
}

// Remove a specific waypoint by index (called from the in-panel list).
// Removing the middle of an N-point route means every subsequent marker's
// number changes, so we tear down the dragging markers and rebuild from
// the coord array. The polyline is re-used (setLatLngs) for cheapness.
export function removeRouteEditWaypoint(index) {
    if (index < 0 || index >= routeEditCoords.length) return;
    routeEditCoords.splice(index, 1);
    if (routeEditCoords.length < 2 && routeEditLine && routeEditLayer) {
        routeEditLayer.removeLayer(routeEditLine);
        routeEditLine = null;
    }
    rebuildRouteEditMarkers();
}

// Returns [waypointCount, totalDistanceNm]
export function getEditRouteStats() {
    const n = routeEditCoords.length;
    if (n < 2) return [n, 0];
    let meters = 0;
    for (let i = 1; i < n; i++) {
        meters += haversineMeters(
            routeEditCoords[i-1][0], routeEditCoords[i-1][1],
            routeEditCoords[i][0], routeEditCoords[i][1]
        );
    }
    return [n, meters * NM_PER_METER];
}

// Load an existing saved route into edit mode for editing.
export function loadRouteForEdit(coords) {
    stopRouteEdit();
    routeEditMode = true;
    routeEditLayer = L.layerGroup().addTo(map);
    for (const c of coords) {
        addEditWaypoint(c[0], c[1]);
    }
}

// --- Polygon editing (freeform region draw) ---
//
// Parallel to route editing but the saved shape is a closed polygon, not
// an open polyline. Vertices are draggable, numbered, and removable via
// the same drag-ghost / renumber / undo flow as routes. When the user
// saves, Map.razor pulls the coords and POSTs them through
// RegionApi.CreatePolygonAsync.

let polygonEditMode = false;
let polygonEditLayer = null;
let polygonEditCoords = [];
let polygonEditMarkers = [];
let polygonEditShape = null;   // L.polygon once there are >= 3 vertices
let polygonEditLine = null;    // L.polyline for 2-vertex preview

const POLYGON_COLOR = '#d4a850';                    // --ann-region
// fillOpacity below drives the translucent fill; the colour is reused
// as both stroke and fill so we don't need a separate fill token.

function makePolygonVertexIcon(num) {
    return L.divIcon({
        className: 'edit-wp-icon',
        html: `<div class="edit-wp-circle edit-poly-circle">${num}</div>`,
        iconSize: [24, 24],
        iconAnchor: [12, 12]
    });
}

function redrawPolygonShape() {
    if (!polygonEditLayer) return;
    // Remove stale shapes; recreate the right one for the current count.
    if (polygonEditCoords.length >= 3) {
        if (polygonEditLine) { polygonEditLayer.removeLayer(polygonEditLine); polygonEditLine = null; }
        if (!polygonEditShape) {
            polygonEditShape = L.polygon(polygonEditCoords, {
                color: POLYGON_COLOR, weight: 2, fillColor: POLYGON_COLOR, fillOpacity: 0.18,
                dashArray: '6,4'
            }).addTo(polygonEditLayer);
        } else {
            polygonEditShape.setLatLngs(polygonEditCoords);
        }
    } else if (polygonEditCoords.length === 2) {
        if (polygonEditShape) { polygonEditLayer.removeLayer(polygonEditShape); polygonEditShape = null; }
        if (!polygonEditLine) {
            polygonEditLine = L.polyline(polygonEditCoords, {
                color: POLYGON_COLOR, weight: 2, dashArray: '6,4', opacity: 0.8
            }).addTo(polygonEditLayer);
        } else {
            polygonEditLine.setLatLngs(polygonEditCoords);
        }
    } else {
        if (polygonEditShape) { polygonEditLayer.removeLayer(polygonEditShape); polygonEditShape = null; }
        if (polygonEditLine) { polygonEditLayer.removeLayer(polygonEditLine); polygonEditLine = null; }
    }
}

// Reuses the route-edit ghost-marker + Delta tooltip. The closure over
// idx captures the current index; removals tear down all markers and
// rebuild, so idx stays in sync with the coords array.
function bindPolygonVertex(marker, idx) {
    let ghostLine = null;
    let ghostMarker = null;
    let origLL = null;

    marker.on('dragstart', (e) => {
        origLL = e.target.getLatLng();
        ghostMarker = L.marker(origLL, {
            icon: L.divIcon({
                className: 'edit-wp-ghost-icon',
                html: '<div class="edit-wp-ghost-circle"></div>',
                iconSize: [24, 24],
                iconAnchor: [12, 12]
            }),
            interactive: false, keyboard: false, zIndexOffset: 500
        }).addTo(polygonEditLayer);
        ghostLine = L.polyline([origLL, origLL], {
            color: POLYGON_COLOR, weight: 1.5, opacity: 0.7, dashArray: '3,4',
            interactive: false
        }).addTo(polygonEditLayer);
        ghostLine.bindTooltip('\u0394 0 m', {
            permanent: true, direction: 'center', className: 'measure-tooltip'
        }).openTooltip(origLL);
    });

    marker.on('drag', (e) => {
        const ll = e.target.getLatLng();
        polygonEditCoords[idx] = [ll.lat, ll.lng];
        redrawPolygonShape();
        if (ghostLine && origLL) {
            ghostLine.setLatLngs([origLL, ll]);
            const dm = haversineMeters(origLL.lat, origLL.lng, ll.lat, ll.lng);
            const label = dm < 1000 ? `\u0394 ${dm.toFixed(0)} m` : `\u0394 ${(dm * NM_PER_METER).toFixed(2)} nm`;
            ghostLine.setTooltipContent(label);
            const tt = ghostLine.getTooltip();
            if (tt) tt.setLatLng(ll);
        }
    });

    marker.on('dragend', () => {
        if (ghostMarker && polygonEditLayer) polygonEditLayer.removeLayer(ghostMarker);
        if (ghostLine && polygonEditLayer) polygonEditLayer.removeLayer(ghostLine);
        ghostMarker = null; ghostLine = null; origLL = null;
    });
}

function addPolygonVertexInternal(lat, lon) {
    const idx = polygonEditCoords.length;
    polygonEditCoords.push([lat, lon]);
    const marker = L.marker([lat, lon], {
        icon: makePolygonVertexIcon(idx + 1),
        draggable: true, zIndexOffset: 800
    }).addTo(polygonEditLayer);
    bindPolygonVertex(marker, idx);
    polygonEditMarkers.push(marker);
    redrawPolygonShape();
}

export function startPolygonEdit() {
    stopPolygonEdit();
    polygonEditMode = true;
    polygonEditLayer = L.layerGroup().addTo(map);
}

export function stopPolygonEdit() {
    polygonEditMode = false;
    if (polygonEditLayer && map) map.removeLayer(polygonEditLayer);
    polygonEditLayer = null;
    polygonEditCoords = [];
    polygonEditMarkers = [];
    polygonEditShape = null;
    polygonEditLine = null;
}

export function getPolygonEditCoords() { return polygonEditCoords; }

// Returns [vertexCount, areaSquareMeters]. Area is 0 below 3 vertices.
// Uses the shoelace formula on the flat projection (close enough at the
// lat scales we care about; a proper geodesic area would be overkill
// for the anchorage / no-go zones sailors draw).
export function getPolygonEditStats() {
    const n = polygonEditCoords.length;
    if (n < 3) return [n, 0];
    // Equirectangular approximation anchored at the first vertex.
    const lat0 = polygonEditCoords[0][0] * Math.PI / 180;
    const cosLat = Math.cos(lat0);
    const METERS_PER_DEG_LAT = 111_320.0;
    const metersPerDegLon = METERS_PER_DEG_LAT * cosLat;
    let area2 = 0;
    for (let i = 0; i < n; i++) {
        const [lat1, lon1] = polygonEditCoords[i];
        const [lat2, lon2] = polygonEditCoords[(i + 1) % n];
        const x1 = lon1 * metersPerDegLon, y1 = lat1 * METERS_PER_DEG_LAT;
        const x2 = lon2 * metersPerDegLon, y2 = lat2 * METERS_PER_DEG_LAT;
        area2 += (x1 * y2) - (x2 * y1);
    }
    return [n, Math.abs(area2) / 2];
}

export function undoLastPolygonVertex() {
    if (polygonEditCoords.length === 0) return;
    polygonEditCoords.pop();
    const last = polygonEditMarkers.pop();
    if (last && polygonEditLayer) polygonEditLayer.removeLayer(last);
    redrawPolygonShape();
}

export function removePolygonEditVertex(index) {
    if (index < 0 || index >= polygonEditCoords.length) return;
    polygonEditCoords.splice(index, 1);
    for (const m of polygonEditMarkers) {
        if (polygonEditLayer) polygonEditLayer.removeLayer(m);
    }
    polygonEditMarkers = [];
    // Rebuild markers without re-entering addPolygonVertexInternal (which
    // would redraw the polygon per vertex -- O(n^2)). Build the markers
    // in one pass, then call redrawPolygonShape() once at the end.
    for (let i = 0; i < polygonEditCoords.length; i++) {
        const [lat, lon] = polygonEditCoords[i];
        const marker = L.marker([lat, lon], {
            icon: makePolygonVertexIcon(i + 1),
            draggable: true, zIndexOffset: 800
        }).addTo(polygonEditLayer);
        bindPolygonVertex(marker, i);
        polygonEditMarkers.push(marker);
    }
    redrawPolygonShape();
}

// --- Waypoint Markers ---

const waypointMarkers = new MarkerLayer();

// Waypoint marker colour. Terracotta is a step warmer/redder than the
// route amber so a bare waypoint reads distinct from a route dot.
const WAYPOINT_COLOR = '#c76f51';

export function addWaypointMarker(id, lat, lon, name) {
    if (!map || waypointMarkers.has(id)) return;
    const marker = L.circleMarker([lat, lon], {
        radius: 6, color: WAYPOINT_COLOR, fillColor: WAYPOINT_COLOR, fillOpacity: 1, weight: 2
    }).addTo(map);
    marker.bindTooltip(name || id.substring(0, 8), {
        permanent: false, direction: 'right', offset: [10, 0],
        className: 'bearing-tooltip'
    });
    waypointMarkers.set(id, marker);
}

// --- Note Markers ---
// Geolocated text annotations (SignalK /resources/notes). Rendered as a
// small folded-page pin that reads distinct from waypoints (circular)
// and routes (amber line). Click opens a popup with title + description
// and a Delete button that round-trips to C# via the cached dotNetRef.

const noteMarkers = new MarkerLayer();
// Note pin colour: darker amber in the same user-annotation family as
// routes (#e09f3e) and waypoints (#c76f51). Deliberate move from the
// previous slate-blue -- blue-on-blue-water tested poorly, and one
// hue family across all user-placed objects is visually coherent.
// Shape (folded-page vs circle vs line) carries the "this is a note"
// signal, not hue.
const NOTE_COLOR = '#c8892e';
const NOTE_COLOR_STROKE = '#7a5418';

function makeNoteIcon() {
    // Folded-page pin, 20x24. Subtle drop shadow so it reads on land or
    // water. Anchor is bottom-centre so the tip of the pin lands on the
    // map coordinate.
    const svg = `
        <svg width="20" height="24" viewBox="0 0 20 24" xmlns="http://www.w3.org/2000/svg"
             style="filter: drop-shadow(0 1px 2px rgba(0,0,0,0.4));">
            <path d="M3 2 L14 2 L17 5 L17 18 L3 18 Z"
                  fill="${NOTE_COLOR}" stroke="${NOTE_COLOR_STROKE}" stroke-width="1.2"
                  stroke-linejoin="round"/>
            <path d="M14 2 L14 5 L17 5" fill="none"
                  stroke="${NOTE_COLOR_STROKE}" stroke-width="1.2" stroke-linejoin="round"/>
            <line x1="6" y1="8"  x2="14" y2="8"  stroke="${NOTE_COLOR_STROKE}" stroke-width="0.9" opacity="0.7"/>
            <line x1="6" y1="11" x2="14" y2="11" stroke="${NOTE_COLOR_STROKE}" stroke-width="0.9" opacity="0.7"/>
            <line x1="6" y1="14" x2="11" y2="14" stroke="${NOTE_COLOR_STROKE}" stroke-width="0.9" opacity="0.7"/>
            <polygon points="10,18 7,22 13,22" fill="${NOTE_COLOR}" stroke="${NOTE_COLOR_STROKE}" stroke-width="1.2" stroke-linejoin="round"/>
        </svg>`;
    return L.divIcon({
        className: 'note-icon',
        html: svg,
        iconSize: [20, 24],
        iconAnchor: [10, 24],
        popupAnchor: [0, -22],
    });
}

let noteIconCached = null;
function getNoteIcon() {
    if (!noteIconCached) noteIconCached = makeNoteIcon();
    return noteIconCached;
}

export function addNoteMarker(id, lat, lon, title, description) {
    if (!map || noteMarkers.has(id)) return;
    const marker = L.marker([lat, lon], { icon: getNoteIcon() }).addTo(map);
    marker.bindPopup(buildNotePopupHtml(id, title, description), {
        className: 'note-popup',
        maxWidth: 280,
        autoClose: true,
    });
    // Wire up the delete button when the popup opens. We query within the
    // popup DOM so an id collision with something else on the page can't
    // hijack the click.
    marker.on('popupopen', (ev) => wireDeleteConfirm(ev.popup, '.note-delete-btn', 'DeleteNote', id));
    noteMarkers.set(id, marker);
}

// Two-step confirm wiring for a popup's delete button. First click
// swaps the label to "Really delete?" and adds a `.confirming` class
// for the red-warning styling; second click within 3 seconds triggers
// the actual server delete via the C# [JSInvokable] method. A passing
// tap in rough weather is the nightmare case; the confirm-and-timeout
// pattern matches how native iOS/Android apps guard destructive
// actions without pulling up a full confirm dialog.
function wireDeleteConfirm(popup, selector, dotNetMethod, id) {
    const el = popup.getElement();
    if (!el) return;
    const btn = el.querySelector(selector);
    if (!btn || btn._wired) return;
    btn._wired = true;
    const originalLabel = btn.textContent;
    let confirmTimer = null;
    const reset = () => {
        btn.classList.remove('confirming');
        btn.textContent = originalLabel;
        if (confirmTimer) { clearTimeout(confirmTimer); confirmTimer = null; }
    };
    btn.addEventListener('click', () => {
        if (!btn.classList.contains('confirming')) {
            btn.classList.add('confirming');
            btn.textContent = 'Really delete?';
            confirmTimer = setTimeout(reset, 3000);
            return;
        }
        reset();
        if (dotNetRef) dotNetRef.invokeMethodAsync(dotNetMethod, id);
    });
    // Closing the popup resets confirm state so re-opening starts fresh.
    popup.once('popupclose', reset);
}

function buildNotePopupHtml(id, title, description) {
    const safeTitle = esc(title || '(untitled)');
    const safeDesc = description ? esc(description).replace(/\n/g, '<br/>') : '';
    return `
        <div class="note-popup-inner">
            <div class="note-popup-title">${safeTitle}</div>
            ${safeDesc ? `<div class="note-popup-body">${safeDesc}</div>` : ''}
            <button class="note-delete-btn" type="button">Delete</button>
        </div>`;
}

export function removeNoteMarker(id) { noteMarkers.remove(id); }

export function clearNotes() { noteMarkers.clear(); }

// Pan the map to a given lat/lon without changing the current zoom.
// Used by the layers-panel "Focus" button on notes (and potentially
// other resources that need a "show me where this is" action).
export function panTo(lat, lon) {
    if (!map) return;
    map.panTo([lat, lon]);
}

// Return the current map centre as [lat, lon]. Used by the FAB
// menu so Create-at-map-centre actions can reuse the same target
// fields the context-menu handlers already write.
export function getMapCenter() {
    if (!map) return null;
    const c = map.getCenter();
    return [c.lat, c.lng];
}

// Open the popup on a note marker if it's currently rendered. No-op
// when the id isn't present (note not yet loaded, or notes hidden).
export function openNotePopup(id) {
    const m = noteMarkers.get(id);
    if (m) m.openPopup();
}

// --- Region (polygon/circle areas) ---
// Rendered as translucent filled polygons with a stronger border.
// Colour is a muted lavender-gray that doesn't collide with routes
// (amber), waypoints (terracotta), notes (slate-blue) or the AIS
// palette. Phase 1 shows them; Phase 2 lets the user create circles
// via a polygon-approximation.

const regionLayers = new MarkerLayer();
// Region stroke: amber-gold in the user-annotation family. Same
// rationale as notes: one hue family for every user-placed object,
// shape carries the meaning. Lighter than the note pin so a pin
// over a region doesn't read as "same colour blob".
const REGION_STROKE = '#d4a850';
const REGION_FILL = 'rgba(212, 168, 80, 0.18)';

// rings: [[[lat, lon], ...], ...]  -- one or more outer rings.
// A MultiPolygon region passes multiple rings; most regions are a
// single Polygon, so `rings` is a one-element array.
export function addRegion(id, rings, title, description) {
    if (!map || regionLayers.has(id)) return;
    if (!Array.isArray(rings) || rings.length === 0) return;
    const group = L.layerGroup();
    const popupHtml = buildRegionPopupHtml(id, title, description);
    for (const ring of rings) {
        const poly = L.polygon(ring, {
            color: REGION_STROKE,
            fillColor: REGION_STROKE,
            fillOpacity: 0.18,
            weight: 1.8,
            opacity: 0.85,
        });
        poly.bindPopup(popupHtml, { className: 'region-popup', maxWidth: 280 });
        poly.on('popupopen', (ev) => wireDeleteConfirm(ev.popup, '.region-delete-btn', 'DeleteRegion', id));
        group.addLayer(poly);
    }
    group.addTo(map);
    regionLayers.set(id, group);
}

// Own-boat popup: same shape as the AIS popup minus the vessel-lookup
// links / buddy toggle. Rebuilt on every popupopen from the marker's
// latest data snapshot so the numbers track nav updates live.
function buildSelfPopupHtml(data) {
    const lat = data.lat, lon = data.lon;
    const sog = data.sogMs != null ? (data.sogMs * 1.94384).toFixed(1) : '--';
    const cogDeg = data.cogRad != null ? (data.cogRad * DEG).toFixed(0) : '--';
    const hdgDeg = data.headingRad != null ? (data.headingRad * DEG).toFixed(0) : '--';
    const pos = (lat != null && lon != null)
        ? `${lat.toFixed(5)}, ${lon.toFixed(5)}`
        : '--';
    return `<div class="ais-popup-content">` +
        `<div class="ais-popup-title">&#9733; Own boat</div>` +
        `<table class="ais-popup-table">` +
            `<tr><td>Pos</td><td>${pos}</td></tr>` +
            `<tr><td>SOG</td><td>${sog} kn</td></tr>` +
            `<tr><td>COG</td><td>${cogDeg}&deg;</td></tr>` +
            `<tr><td>HDG</td><td>${hdgDeg}&deg;</td></tr>` +
        `</table>` +
        `</div>`;
}

function buildRegionPopupHtml(id, title, description) {
    const safeTitle = esc(title || '(untitled region)');
    const safeDesc = description ? esc(description).replace(/\n/g, '<br/>') : '';
    return `
        <div class="region-popup-inner">
            <div class="region-popup-title">${safeTitle}</div>
            ${safeDesc ? `<div class="region-popup-body">${safeDesc}</div>` : ''}
            <button class="region-delete-btn" type="button">Delete</button>
        </div>`;
}

export function removeRegion(id) { regionLayers.remove(id); }

export function clearRegions() { regionLayers.clear(); }

// ---- Region circle preview ----------------------------------------
// Light-weight circle drawn while the Add-Region dialog is open in
// Circle mode. Shares the region amber so the user sees the final
// shape at real size before committing. Replaced on every radius tap.

let circlePreviewLayer = null;

export function setCirclePreview(lat, lon, radiusMeters) {
    if (!map) return;
    if (circlePreviewLayer) {
        circlePreviewLayer.setLatLng([lat, lon]);
        circlePreviewLayer.setRadius(radiusMeters);
        return;
    }
    circlePreviewLayer = L.circle([lat, lon], {
        radius: radiusMeters,
        color: REGION_STROKE,
        fillColor: REGION_STROKE,
        fillOpacity: 0.12,
        weight: 1.6,
        dashArray: '4,4',
        interactive: false,
    }).addTo(map);
}

export function clearCirclePreview() {
    if (circlePreviewLayer && map) {
        map.removeLayer(circlePreviewLayer);
        circlePreviewLayer = null;
    }
}

// Pan to a region and open its popup. Accepts the first ring and
// uses its bounds so we frame whatever the user clicked in the
// Layers panel.
export function focusRegion(id, firstRing) {
    const layer = regionLayers.get(id);
    if (!layer || !map) return;
    if (Array.isArray(firstRing) && firstRing.length > 0) {
        const bounds = L.latLngBounds(firstRing);
        map.fitBounds(bounds, { padding: [40, 40], maxZoom: 14 });
    }
    // Open popup on the first polygon in the group.
    layer.eachLayer(l => { if (l.openPopup) l.openPopup(); return false; });
}

// --- Weather Overlay ---

let weatherLayer = null;

// Weather / radar overlay. Currently wired to RainViewer radar-precipitation
// tiles, which top out around z=12 server-side. We cap fetches at that
// level via maxNativeZoom and let Leaflet upscale (maxZoom 22 to match
// the map) so pinching further in just blurs the nowcast instead of
// erroring out with "zoom level not supported" from the upstream CDN.
// (OpenWeatherMap would need an API key -- not threaded through yet.)
export function setWeatherOverlay(tileUrl) {
    clearWeatherOverlay();
    if (!map || !tileUrl) return;
    weatherLayer = L.tileLayer(tileUrl, {
        maxNativeZoom: 12,
        maxZoom: 22,
        opacity: 0.5,
        errorTileUrl: '',
        attribution: '&copy; RainViewer'
    }).addTo(map);
    weatherLayer.setZIndex(40); // Below chart layers (50) but above base map.
}

export function clearWeatherOverlay() {
    if (weatherLayer && map) { map.removeLayer(weatherLayer); weatherLayer = null; }
}

// --- File I/O helpers (GPX import/export) ---

export function triggerFileDownload(filename, content) {
    const blob = new Blob([content], { type: 'application/gpx+xml' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    a.click();
    URL.revokeObjectURL(url);
}

// --- Tidal Current Arrow ---

let currentArrow = null;
let currentLabel = null;

export function setCurrentArrow(boatLat, boatLon, setRad, driftMs) {
    if (!map) return;
    const driftKn = driftMs * 1.94384;
    // Arrow length proportional to drift, min 200m, max 2000m visual.
    const arrowLen = Math.min(Math.max(driftMs * 600, 200), 2000);
    const endPt = destPoint(boatLat, boatLon, setRad, arrowLen);

    if (currentArrow) {
        currentArrow.setLatLngs([[boatLat, boatLon], endPt]);
    } else {
        currentArrow = L.polyline([[boatLat, boatLon], endPt], {
            color: '#a78bfa', weight: 3, opacity: 0.8
        }).addTo(map);
    }

    const labelText = `${driftKn.toFixed(1)}kn`;
    if (currentLabel) {
        currentLabel.setLatLng(endPt);
        currentLabel.setContent(labelText);
    } else {
        currentLabel = L.tooltip({
            permanent: true, direction: 'right', offset: [6, 0],
            className: 'bearing-tooltip'
        }).setLatLng(endPt).setContent(labelText).addTo(map);
    }
}

export function clearCurrentArrow() {
    if (currentArrow && map) { map.removeLayer(currentArrow); currentArrow = null; }
    if (currentLabel && map) { map.removeLayer(currentLabel); currentLabel = null; }
}

// --- Laylines ---

// Draw port/starboard laylines from boat position (and optionally from waypoint).
// twdRad = true wind direction (radians, FROM north). twaRad = true wind angle (radians, absolute).
export function setLaylines(boatLat, boatLon, twdRad, twaRad, wpLat, wpLon) {
    if (!map) return;

    const lineLen = 5 * 1852; // 5 nm in meters
    const absTwa = Math.abs(twaRad);

    // Boat sails INTO the wind: TWD + PI gives the "to" direction, +/- TWA gives tack angles.
    const stbdBrg = twdRad + Math.PI - absTwa;
    const portBrg = twdRad + Math.PI + absTwa;

    const stbdEnd = destPoint(boatLat, boatLon, stbdBrg, lineLen);
    const portEnd = destPoint(boatLat, boatLon, portBrg, lineLen);

    if (laylineStarboard) laylineStarboard.setLatLngs([[boatLat, boatLon], stbdEnd]);
    else {
        laylineStarboard = L.polyline([[boatLat, boatLon], stbdEnd], {
            color: '#22c55e', weight: 2, opacity: 0.7, dashArray: '10,6'
        }).addTo(map);
    }

    if (laylinePort) laylinePort.setLatLngs([[boatLat, boatLon], portEnd]);
    else {
        laylinePort = L.polyline([[boatLat, boatLon], portEnd], {
            color: '#ef4444', weight: 2, opacity: 0.7, dashArray: '10,6'
        }).addTo(map);
    }

    // Waypoint laylines (from waypoint back toward the wind).
    if (wpLat != null && wpLon != null) {
        const wpStbdEnd = destPoint(wpLat, wpLon, stbdBrg + Math.PI, lineLen);
        const wpPortEnd = destPoint(wpLat, wpLon, portBrg + Math.PI, lineLen);

        if (laylineWpStarboard) laylineWpStarboard.setLatLngs([[wpLat, wpLon], wpStbdEnd]);
        else {
            laylineWpStarboard = L.polyline([[wpLat, wpLon], wpStbdEnd], {
                color: '#22c55e', weight: 1.5, opacity: 0.35, dashArray: '6,6'
            }).addTo(map);
        }
        if (laylineWpPort) laylineWpPort.setLatLngs([[wpLat, wpLon], wpPortEnd]);
        else {
            laylineWpPort = L.polyline([[wpLat, wpLon], wpPortEnd], {
                color: '#ef4444', weight: 1.5, opacity: 0.35, dashArray: '6,6'
            }).addTo(map);
        }
    } else {
        if (laylineWpStarboard && map) { map.removeLayer(laylineWpStarboard); laylineWpStarboard = null; }
        if (laylineWpPort && map) { map.removeLayer(laylineWpPort); laylineWpPort = null; }
    }
}

export function clearLaylines() {
    if (laylineStarboard && map) { map.removeLayer(laylineStarboard); laylineStarboard = null; }
    if (laylinePort && map) { map.removeLayer(laylinePort); laylinePort = null; }
    if (laylineWpStarboard && map) { map.removeLayer(laylineWpStarboard); laylineWpStarboard = null; }
    if (laylineWpPort && map) { map.removeLayer(laylineWpPort); laylineWpPort = null; }
}

// --- Keyboard shortcuts ---

let keyHandler = null;

export function enableKeyboardShortcuts(dotNetObjRef) {
    disableKeyboardShortcuts();
    keyHandler = (e) => {
        // Skip if user is typing in an input.
        if (e.target.tagName === 'INPUT' || e.target.tagName === 'TEXTAREA') return;
        const key = e.key.toLowerCase();
        const isLetter = 'mfnatlor'.includes(key) && key.length === 1;
        const isSpecial = key === '?' || key === 'escape';
        if (isLetter || isSpecial) {
            e.preventDefault();
            dotNetObjRef.invokeMethodAsync('OnKeyShortcut', key);
        }
    };
    document.addEventListener('keydown', keyHandler);
}

export function disableKeyboardShortcuts() {
    if (keyHandler) { document.removeEventListener('keydown', keyHandler); keyHandler = null; }
}

// --- Controls ---

export function setFollow(follow) { followBoat = follow; }

// Map orientation: 'north' (default), 'course' (rotates to COG), 'head' (rotates to heading).
export function setMapOrientation(mode) {
    mapOrientation = mode;
    if (mode === 'north') {
        applyMapRotation(0);
    }
    // For 'course' and 'head', rotation is applied in updatePosition.
}

function applyMapRotation(deg) {
    if (!map) return;
    currentRotationDeg = deg;
    const container = map.getContainer();
    container.style.transform = deg === 0 ? '' : `rotate(${-deg}deg)`;
    container.style.transformOrigin = 'center center';
    // Counter-rotate labels/tooltips so they stay upright.
    const style = document.getElementById('map-rotation-style');
    if (deg === 0) {
        if (style) style.remove();
    } else {
        const css = `.leaflet-tooltip, .leaflet-popup, .ais-label, .vector-label, .route-wp-tooltip, .bearing-tooltip { transform: rotate(${deg}deg) !important; }`;
        if (style) {
            style.textContent = css;
        } else {
            const el = document.createElement('style');
            el.id = 'map-rotation-style';
            el.textContent = css;
            document.head.appendChild(el);
        }
    }
    // Invalidate map size after rotation.
    map.invalidateSize();
}

export function zoomToTrack() {
    if (!trackLayer || !map) return;
    if (typeof trackLayer.getBounds !== 'function') return; // older map instance
    const bounds = trackLayer.getBounds();
    if (bounds && bounds.isValid()) {
        map.fitBounds(bounds, { padding: [40, 40], maxZoom: 16 });
        return;
    }
    // No track yet: fall back to the boat's position if we have one.
    if (boatMarker) map.setView(boatMarker.getLatLng(), Math.max(map.getZoom(), 13));
}

export function dispose() {
    if (map) { map.remove(); map = null; }
    boatMarker = null; boatVector = null; vectorLabel = null; trackLayer = null;
    osmBaseLayer = null; seaBaseLayer = null; serverTrackLayer = null;
    weatherRouteLayer = null;
    chartLayers.clear();
    routeLayers.clear();
    for (const id of Object.keys(aisLabels)) delete aisLabels[id];
    bearingLine = null; bearingLabel = null;
    mobMarker = null; mobCircle = null; mobLine = null; mobLabel = null;
    anchorMarker = null; anchorCircle = null; anchorTrailLayer = null;
    anchorTrail.length = 0;
    activeRouteLayer = null; activeRouteCoords = null; nextWpMarker = null;
    courseLineLeg = null; courseLineBearing = null; courseLineXte = null;
    laylineStarboard = null; laylinePort = null;
    laylineWpStarboard = null; laylineWpPort = null;
    currentArrow = null; currentLabel = null;
    weatherLayer = null;
    routeEditMode = false; routeEditLayer = null;
    routeEditCoords = []; routeEditMarkers = []; routeEditLine = null;
    polygonEditMode = false; polygonEditLayer = null;
    polygonEditCoords = []; polygonEditMarkers = [];
    polygonEditShape = null; polygonEditLine = null;
    waypointMarkers.clear();
    noteMarkers.clear();
    regionLayers.clear();
    for (const ctx of Object.keys(aisMarkers)) delete aisMarkers[ctx];
    for (const ctx of Object.keys(aisVectors)) delete aisVectors[ctx];
    for (const ctx of Object.keys(aisCpaOwnLines)) delete aisCpaOwnLines[ctx];
    for (const ctx of Object.keys(aisCpaTgtLines)) delete aisCpaTgtLines[ctx];
    for (const ctx of Object.keys(aisCpaLabels)) delete aisCpaLabels[ctx];
    for (const ctx of Object.keys(aisTrailLines)) delete aisTrailLines[ctx];
    for (const ctx of Object.keys(aisTrailHistory)) delete aisTrailHistory[ctx];
    guardZoneRing = null;
    dotNetRef = null;
}
