// Leaflet JS interop for the chartplotter map.
// All map state lives here; Blazor calls exported functions via IJSRuntime.

import { RAD, DEG, NM_PER_METER, VECTOR_MINUTES, SPEED_BUCKETS,
         haversineMeters, bearingDeg, destPoint, vectorEnd,
         speedColor, speedBucket } from './geoMath.js';
import { MarkerLayer } from './markerLayer.js';
import { enableRadarOverlay, disableRadarOverlay,
         setRadarRange, setBoatState as setRadarBoatState } from './radarLayer.js';

let map = null;
// Module-scoped bounds-debounce timer so dispose() can cancel it.
// If a pending moveend callback fired after dispose() nulled map,
// it would hit the "addLayer on null" chain through Leaflet's
// internal layer lookups. See the moveend handler in initMap.
let boundsTimer = null;
// Weak-client detection (Raspberry Pi, older tablets). Gates
// perf-heavy options -- tile streaming during pan, AIS updates
// during active drag, etc. Computed once in initMap() so the
// same flag drives every layer created later.
let isSlowClient = false;
// Own-boat MMSI, pushed from C# once SignalkClient.SetSelfContext
// resolves (the hello message). Used by buildSelfPopupHtml to pull
// the country flag from the same signalk-flags endpoint the AIS
// popups use. Null until resolved; empty string means "no mmsi on
// the self URN" (rare but valid for inland boats without AIS).
let ownMmsi = null;
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

// Set of MMSIs we've already kicked a flag-image fetch for. The flag
// lives behind /signalk/v2/api/resources/flags/mmsi/{mmsi} via the
// signalk-flags plugin; lazy-loading only on popup-open gave a visible
// flash as the user scrolled through AIS targets in a busy harbour.
// Pre-warming on the first updateAisTargets tick lets the browser cache
// handle subsequent opens. Size unbounded; in a typical passage this
// sits at a few hundred entries -- trivial.
const flagsPrewarmed = new Set();
function prewarmFlag(mmsi) {
    if (!mmsi || flagsPrewarmed.has(mmsi)) return;
    flagsPrewarmed.add(mmsi);
    const img = new Image();
    // Image() doesn't block, no onerror noise (plugin-missing fetches
    // are absorbed silently since no element is attached to the DOM).
    img.src = flagUrl(mmsi);
}

// SignalK server base URL (scheme+host+port, no trailing slash).
// Pushed in from Blazor via setSignalKBaseUrl() during init so all
// origin-bound URLs (flag images, future direct API hits) target
// the right host. Empty string falls through to page-relative,
// which only works when the SK server lives on the same origin
// (the legacy localhost:3000 setup).
let signalKBaseUrl = '';

export function setSignalKBaseUrl(url) {
    signalKBaseUrl = (url || '').replace(/\/+$/, '');
}

/** Build the flag-image URL for an MMSI. Same path on every server
 *  (signalk-flags plugin); only the origin varies. */
function flagUrl(mmsi) {
    return `${signalKBaseUrl}/signalk/v2/api/resources/flags/mmsi/${encodeURIComponent(mmsi)}`;
}
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

// MarkerLayer now lives in markerLayer.js so it's unit-testable in
// node (see markerLayer.test.js). Each instance needs a reference to
// the Leaflet map for removeLayer() -- since `map` gets assigned in
// initMap *after* these dicts are constructed, MarkerLayer takes the
// map reference lazily via setMap() below, once initMap runs.

// Chart layers from SignalK.
const chartLayers = new MarkerLayer();  // keyed by chart identifier
let osmBaseLayer = null;
let seaBaseLayer = null;

// Zoom-level badge (bottom-right). Assigned in initMap so the control
// exists before the first zoomend fires.
let zoomBadge = null;

// Wake-lock code used to live here; moved to wakeLock.js so it can
// be held across every page, not just the Map. MainLayout manages it
// now.

// Zoom-level badge. Compact "z N" at glance; an amber "↑" appears
// when the map is zoomed past the top chart's native max so tiles
// are being scaled up. Full "native K" text moves to the title
// tooltip rather than widening the badge -- the bottom-left row
// was pushing the depth HUD upward when the badge grew mid-pan.
const ZoomBadge = L.Control.extend({
    onAdd() {
        this._el = L.DomUtil.create('div', 'ona-zoom-badge');
        L.DomEvent.disableClickPropagation(this._el);
        return this._el;
    },
    update() {
        if (!this._el || !this._map) return;
        const z = this._map.getZoom();
        // Overzoom-awareness stripped (feature removed); the badge is
        // now a plain zoom-level indicator.
        this._el.textContent = `z${z}`;
        this._el.title = `Zoom ${z}`;
    }
});

// Routes and server track.
const routeLayers = new MarkerLayer();  // keyed by route ID
let serverTrackLayer = null;

// Active route navigation.
let activeRouteLayer = null;   // L.layerGroup: full route polyline + waypoint markers
// True while the helm is editing the currently-active route. Used
// to suppress redraws of the active polyline + course-line by
// applyFrame so they don't fight the edit-mode polyline. Toggled
// from C# via setActiveOverlayHidden when EditRoute / CancelRoute /
// SaveRoute fire on the active route.
let activeOverlayHidden = false;
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

// MOB state.
let mobMarker = null;
let mobCircle = null;
let mobLine = null;
let mobLabel = null;

// Anchor watch state.
let anchorMarker = null;
let anchorCircle = null;
// Radius line (boat -> anchor) + "Xm" label. Turn visual feedback
// "where is the anchor / how much rode is out" into a first-class
// affordance instead of only showing the watch circle.
let anchorRadiusLine = null;
// (anchorRadiusLabel removed -- the midpoint distance chip was dropped
// per user request; the boat<->anchor line alone now communicates the
// radius implicitly relative to the alarm circle.)
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

// --- Map colour palette -----------------------------------------
// Single source of truth lives in app.css as --map-* custom
// properties. LegendOverlay swatches and the Leaflet overlays below
// both pull from there, so a palette edit updates chart + legend in
// lockstep. readMapColors() is called inside initMap once the
// stylesheet is guaranteed parsed; the defaults below are kept only
// as safety net for a broken CSS build so the chart doesn't render
// black-on-black. Keep them aligned with app.css :root for sanity.
const MapColors = {
    own: '#ec4899',
    cogVector: '#f9a8d4',
    danger: '#c4453e',
    buddy: '#e9c46a',
    radar: '#b08d5a',
    current: '#a78bfa',
    bearing: '#06b6d4',
    courseLeg: '#e2e8f0',
    mob: '#ef4444',
    anchorOk: '#22c55e',
    anchorDrag: '#ef4444',
    route: '#e09f3e',         // --ann-route
    guardWarn: '#f59e0b',     // --sev-warn
};

function readMapColors() {
    if (typeof document === 'undefined') return;
    const root = getComputedStyle(document.documentElement);
    const pick = (name, fallback) => {
        const v = root.getPropertyValue(name).trim();
        return v || fallback;
    };
    MapColors.own        = pick('--map-own',        MapColors.own);
    MapColors.cogVector  = pick('--map-cog-vector', MapColors.cogVector);
    MapColors.danger     = pick('--map-danger',     MapColors.danger);
    MapColors.buddy      = pick('--map-buddy',      MapColors.buddy);
    MapColors.radar      = pick('--map-radar',      MapColors.radar);
    MapColors.current    = pick('--map-current',    MapColors.current);
    MapColors.bearing    = pick('--map-bearing',    MapColors.bearing);
    MapColors.courseLeg  = pick('--map-course-leg', MapColors.courseLeg);
    MapColors.mob        = pick('--map-mob',        MapColors.mob);
    MapColors.anchorOk   = pick('--map-anchor-ok',  MapColors.anchorOk);
    MapColors.anchorDrag = pick('--map-anchor-drag', MapColors.anchorDrag);
    MapColors.route      = pick('--ann-route',      MapColors.route);
    MapColors.guardWarn  = pick('--sev-warn',       MapColors.guardWarn);
}

// selfIcon is rebuilt on each initMap() call so a fresh palette read
// is reflected. Previously it was a module-level const built before
// CSS had a chance to load, which locked the magenta hex even after
// the stylesheet defined a new --map-own.
let selfIcon = null;

// (Legacy module-level colour consts removed -- call sites now read
// MapColors.* directly so readMapColors() at init time is the one
// event that decides the runtime palette.)
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
// AIS icon size. 28 leaves own boat (30) visibly bigger while making
// other traffic actually legible at chart zoom. 24 previously read as
// "too small" on a helm screen, especially with a ship-type glyph
// overlaid -- the glyph shrank to noise.
const AIS_ICON_SIZE = 28;
const RADAR_ICON_SIZE = 26;

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

// ========== EXPORTED FUNCTIONS ==========

export function initMap(elementId, lat, lon, zoom, dotNetObjRef) {
    if (map) map.remove();
    dotNetRef = dotNetObjRef;

    // Pull the --map-* palette out of the stylesheet now that it's
    // parsed, then build the own-boat icon from the refreshed value.
    // Doing this inside initMap (instead of at module load) means a
    // palette tweak applied via :root takes effect without touching
    // the JS -- the one edit site is app.css.
    readMapColors();
    selfIcon = makeIcon(makeBoatSvg(MapColors.own, 30, true), 30);

    // Detect weak client BEFORE building the map so the renderer choice
    // below can flip with it. Heuristic: 4-or-fewer logical cores (Pi)
    // or 'arm'/'raspberry' in the UA. isSlowClient is a module-scope
    // let because other functions (tile layer creation, AIS updates)
    // also consult it later.
    isSlowClient =
        (typeof navigator !== 'undefined' && navigator.hardwareConcurrency
            && navigator.hardwareConcurrency <= 4)
        || /\barm\b|\barmv|raspberry/i.test(
            typeof navigator !== 'undefined' ? (navigator.userAgent || '') : '');

    // Mirror the flag to the DOM so CSS can strip perf-heavy effects
    // (backdrop-filter blur passes, drop-shadow filters) on slow
    // hardware. A single data-attribute drives all the :not(...) overrides
    // in app.css, so we don't have to thread the flag into every rule.
    try {
        document.documentElement.dataset.slowClient = isSlowClient ? '1' : '0';
    } catch (_) { /* SSR / no DOM -- ignore */ }

    // preferCanvas: true routes all L.polyline / L.polygon / L.circle /
    // L.circleMarker draw calls through a single HTML canvas instead of
    // per-layer SVG elements. For 100+ AIS trails / vectors / guard
    // rings on a Raspi this collapses many compositor layers into one,
    // cutting both paint cost and memory. Markers with divIcons (boat,
    // AIS chevrons) stay SVG/HTML so we don't lose their custom styling.
    // Gated on slow-client so desktop / iPad keep the SVG renderer that
    // gives crisper outlines at high DPI.
    //
    // Map maxZoom matches the typical base-tile native cap (19). The
    // overzoom-past-native feature was removed; adding it back later
    // would bump this to 22 and re-introduce per-layer maxNativeZoom
    // management.
    map = L.map(elementId, {
        zoomControl: false,
        maxZoom: 19,
        preferCanvas: isSlowClient,
        // Half-step zoom. Default 1.0 jumps a full power-of-two per
        // tap which is too coarse for chart work -- the helm sees a
        // 2x scale change when usually 1.4x is what's wanted to nudge
        // detail in / out. zoomSnap: 0.5 + zoomDelta: 0.5 lands every
        // mouse / topbar tap at half-integer levels (17.5, 18.0, 18.5).
        // Pinch + wheel inherit the same snap. Matches Freeboard-SK's
        // behaviour where its OpenLayers view allows fractional zoom
        // by default.
        zoomSnap: 0.5,
        zoomDelta: 0.5,
        // Leaflet's default wheelPxPerZoomLevel = 60 means a 100 px
        // wheel tick (the value Linux/X11 reports for one notch on a
        // standard mouse) zooms ~1.66 levels per tick, which the user
        // reads as "two steps". Bumping to 100 makes one mouse-wheel
        // notch == one zoom level on Linux. macOS / Windows trackpads
        // and high-resolution wheels emit smaller delta-values that
        // accumulate via wheelDebounceTime, so they still zoom
        // smoothly -- this just removes the over-quantisation on the
        // notched mouse path.
        wheelPxPerZoomLevel: 100,
    }).setView([lat, lon], zoom);
    // Drop the "Leaflet |" prefix from the attribution bar. The actual
    // OSM / OpenSeaMap attribution stays (ODbL / CC-BY-SA require it);
    // the Leaflet credit is courtesy and removable.
    map.attributionControl.setPrefix(false);

    // Hook the newly-created Leaflet map into each MarkerLayer dict so
    // remove() / clear() can pull layers off the map. Done here rather
    // than in the ctor because module-scope `new MarkerLayer()` runs
    // before initMap, when `map` is still null.
    for (const ml of [chartLayers, routeLayers, waypointMarkers, noteMarkers, regionLayers]) {
        ml.setMap(map);
    }

    // Leaflet's native +/- zoom control is turned off above
    // (zoomControl: false). The replacement lives in the app topbar
    // (Components/Layout/MainLayout.razor, gated on IsMapRoute) and
    // calls zoomIn / zoomOut below via JS interop. The native 26px
    // control was too small at arm's length in a rolling cockpit;
    // the topbar pair is bigger and reachable one-handed.

    // Zoom-level badge in the bottom-left of the Leaflet control area:
    // "z14" chip so the helm can tell at a glance whether they're at
    // z14 or z16 without poking the +/- buttons until tile detail
    // changes. The metric + nautical scale bars used to live here too
    // but were removed -- the helm asked for a quieter chrome strip
    // and the rose / radar overlays both carry their own range cues
    // already (rings, radar range chip), so the scale was redundant.
    zoomBadge = new ZoomBadge({ position: 'bottomleft' });
    zoomBadge.addTo(map);
    map.on('zoomend', () => zoomBadge.update());
    zoomBadge.update();

    // isSlowClient was set at the top of initMap; the same flag drives
    // tile updateWhenIdle here so all perf gates decide together.
    //
    // keepBuffer bumped above the default 2 so tiles stay in memory a
    // few rings further out; panning back doesn't re-request. Cheap
    // memory, noticeable smoothness on the Pi-local-wifi setup where
    // re-fetch RTT is low but visible.
    // detectRetina: fetches {z+1} tiles and scales to the display grid
    // on high-DPI devices (iPad, most phones). Without it Leaflet uses
    // the raw {z} tile scaled up by devicePixelRatio, which on a DPR=2
    // iPad renders a 256-px source tile into 512 px of screen -- the
    // user-visible "tiles are blurry, even without overzoom" bug.
    // Skipped on slow clients: fetching 4x as many tiles (z+1 quad-tree
    // child) would more than undo the updateWhenIdle win.
    const retina = !isSlowClient;

    // crossOrigin intentionally unset on the base tiles. tile.openstreetmap.org
    // serves `Access-Control-Allow-Origin: *` most of the time, but a cached
    // response from an earlier non-anonymous fetch (browser, corporate proxy,
    // Service-Worker shim) can arrive WITHOUT the header, and Chromium then
    // fails the anonymous request rather than reusing the cached body. We
    // never sample the tiles into a canvas, so the anonymous handshake gives
    // us nothing; dropping it is the fix Freeboard-SK took for the same class
    // of reports.
    osmBaseLayer = L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
        maxNativeZoom: 19,
        maxZoom: 19,
        // keepBuffer 10 (up from 6, default 2): ten extra rings of
        // tiles outside the viewport stay in the DOM, so small pans
        // during route planning don't trigger a fetch -- the next
        // ring is already rendered and just gets revealed. Trade-off
        // is more DOM nodes (~250-400 extra per layer at typical
        // iPad zoom), still negligible on modern WebKit / Chromium
        // tile pipelines, and on the Pi-local-wifi setup where
        // re-fetch RTT is low but visible the smoothness gain is
        // noticeable.
        keepBuffer: 10,
        updateWhenIdle: isSlowClient,
        detectRetina: retina,
        // Minimum acceptable ODbL attribution: short visible text
        // ("© OpenStreetMap") linking to the canonical copyright page
        // (which lists contributors). The conventional "contributors"
        // word is dropped from the visible text so the chip stays
        // small at the edge of the chart -- the link still satisfies
        // the licence's "credit and link" requirement.
        attribution: '<a href="https://www.openstreetmap.org/copyright" target="_blank" rel="noreferrer">&copy; OpenStreetMap</a>',
        referrerPolicy: 'strict-origin-when-cross-origin'
    }).addTo(map);

    seaBaseLayer = L.tileLayer('https://tiles.openseamap.org/seamark/{z}/{x}/{y}.png', {
        maxNativeZoom: 19,
        maxZoom: 19,
        keepBuffer: 10,
        updateWhenIdle: isSlowClient,
        detectRetina: retina,
        attribution: '<a href="https://www.openseamap.org/" target="_blank" rel="noreferrer">&copy; OpenSeaMap</a>',
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
    // closeButton:false matches the AIS-marker popup style (the "x"
    // at the top-right was the only decoration on the own-boat popup
    // and looked like a weird glyph on the chart). User closes by
    // tapping outside or tapping the marker again.
    boatMarker.bindPopup('', { className: 'ais-popup', maxWidth: 260, closeButton: false });
    boatMarker.on('popupopen', () => {
        // In measure mode, tapping the boat means "anchor this leg to the
        // vessel" -- the measurement starts (or continues) from the boat
        // and tracks it as it moves. Swallow the popup so the helm doesn't
        // get the data card flashed up while they're plotting a distance.
        if (measureActive) {
            boatMarker.closePopup();
            addVesselMeasurePoint();
            return;
        }
        const data = boatMarker._onaSelfData || {};
        boatMarker.setPopupContent(buildSelfPopupHtml(data));
    });
    boatVector = L.polyline([], { color: MapColors.cogVector, weight: 1.5, dashArray: '6,4', opacity: 0.8 }).addTo(map);

    // Map click: in route edit mode, add waypoint. In measurement
    // mode, drop a measurement point. Otherwise just dismiss menus.
    map.on('click', (e) => {
        if (routeEditMode) {
            // L.DomEvent.stopPropagation on the polyline click
            // doesn't actually stop Leaflet's map-level click dispatch
            // (different event channels), so a leg-click fires
            // insertEditVertexOnSegment AND then the map-click would
            // also append the same point at the end. The segment
            // handler sets a short-lived suppression flag; we honour
            // it here.
            if (routeEditSuppressNextMapClick) {
                routeEditSuppressNextMapClick = false;
                return;
            }
            addEditWaypoint(e.latlng.lat, e.latlng.lng);
            return;
        }
        if (polygonEditMode) {
            addPolygonVertexInternal(e.latlng.lat, e.latlng.lng);
            return;
        }
        if (measureActive) {
            // Segment-click insertion (insertMeasurePointOnSegment)
            // bubbles into this handler immediately after splicing the
            // new vertex; without the suppression flag we'd then append
            // a duplicate point at the end of the ruler. Same pattern
            // as routeEditSuppressNextMapClick for route edit.
            if (measureSuppressNextMapClick) {
                measureSuppressNextMapClick = false;
                return;
            }
            addMeasurePoint(e.latlng.lat, e.latlng.lng);
            return;
        }
        if (dotNetRef) dotNetRef.invokeMethodAsync('OnDismissContextMenu').catch(() => {});
    });

    // Right-click (desktop) and long-press (touch) -> context menu callback to Blazor.
    function showContextMenu(latlng) {
        if (routeEditMode || polygonEditMode || !dotNetRef) return;
        // In measure mode the right-click / long-press gesture means
        // "reset the current measurement" rather than "open the create-
        // here menu". Wipe the points and stay in measure mode so the
        // helm can immediately start a fresh measurement; opening the
        // context menu over a half-built ruler would just be in the way.
        if (measureActive) {
            clearMeasure();
            return;
        }
        const pt = map.latLngToContainerPoint(latlng);
        const sz = map.getSize();
        const x = Math.min(pt.x, sz.x - 175);
        const y = Math.min(pt.y, sz.y - 125);
        // .catch: dotNetRef can be disposed between the null-check above
        // and the dispatch landing on the C# side. Same pattern across
        // every invokeMethodAsync callsite -- silent-swallow is correct
        // because the target page is already unmounting.
        dotNetRef.invokeMethodAsync('OnMapContextMenu',
            latlng.lat, latlng.lng, Math.max(x, 5), Math.max(y, 5)).catch(() => {});
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

    // A pointerdown anywhere on an alarm banner cancels any pending
    // map long-press. Reason: the map timer can be armed by an
    // earlier touch on the map, then the alarm pops up overlaying
    // the user's finger position. Without this cancel, the user
    // dismisses the alarm and the map's context menu fires a beat
    // later at the original coordinate. Pointerdown on the alarm
    // is a stronger signal of intent than the lingering map timer.
    document.addEventListener('pointerdown', (e) => {
        if (e.target && e.target.closest && e.target.closest('.alarm-banner-stack')) {
            cancelLongPress();
        }
    }, { passive: true });

    mapEl.addEventListener('touchstart', (e) => {
        cancelLongPress();
        if (e.touches.length !== 1) return; // two-finger pinch, etc.
        // Ignore presses that land on a control (buttons, map chrome).
        // .cpa-label is an interactive tooltip that opens the vessel
        // popup on tap; without excluding it, a touch on the CPA chip
        // also armed the long-press context menu, so both the popup
        // AND the context menu fired on the same finger-down.
        // Not excluding .leaflet-interactive on purpose -- the context
        // menu is "create at this point" and the user may very well
        // want that while their finger is over a route line or AIS
        // target; only TOOLTIPS that have their own tap semantics opt
        // out of the long-press.
        if (e.target && e.target.closest && e.target.closest(
            '.leaflet-control, .cpa-label, .leaflet-tooltip, button, a, input, label'))
            return;
        const t0 = e.touches[0];
        longPressStartPt = { x: t0.clientX, y: t0.clientY };
        longPressTimer = setTimeout(() => {
            longPressTimer = null;
            // If the page navigated away while we were waiting for the
            // long-press threshold, map is null and containerPointToLatLng
            // would throw. Bail silently.
            if (!map || !longPressStartPt) return;
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
                buddy.getAttribute('data-is') === '1').catch(() => {});
            return;
        }
        const snooze = e.target.closest('a[data-ona-snooze]');
        if (snooze) {
            e.preventDefault();
            e.stopPropagation();
            dotNetRef.invokeMethodAsync('OnSnoozeVessel',
                snooze.getAttribute('data-ctx') || '',
                snooze.getAttribute('data-nm') || '').catch(() => {});
            return;
        }
    });

    // Notify Blazor when the viewport changes so layers can be filtered by bounds.
    // Debounced: skip events caused by programmatic panTo (follow mode) and coalesce
    // rapid user interactions into a single callback. Same callback also carries
    // centre+zoom so the C# side can persist the map view (mapView.v1) without
    // adding a second interop round-trip per move.
    //
    // The 300 ms debounce is also the window where the user can navigate
    // away before the callback fires. Re-check `map` + `dotNetRef` inside
    // the timeout -- both are nulled on dispose() and calling
    // `map.getBounds()` after that throws the infamous
    // "can't access property addLayer, t is null" via Leaflet's internal
    // layer lookups. Defensive re-check is cheap and covers the race.
    map.on('moveend', () => {
        if (!dotNetRef || suppressMoveEnd) return;
        clearTimeout(boundsTimer);
        boundsTimer = setTimeout(() => {
            if (!map || !dotNetRef) return;
            const b = map.getBounds();
            const c = map.getCenter();
            dotNetRef.invokeMethodAsync('OnMapBoundsChanged',
                b.getWest(), b.getSouth(), b.getEast(), b.getNorth(),
                c.lat, c.lng, map.getZoom()).catch(() => { /* disposed */ });
        }, 300);
    });

    // Fire OnMapBoundsChanged ONCE on init with the initial viewport.
    // Moveend won't fire until the user actually pans, so without this
    // the Layers panel lists every chart/route regardless of what's
    // visible until the first interaction -- user-reported "bounds
    // aren't respected on first load". Timeout = 0 lets Leaflet settle
    // its first render (size, CRS) before getBounds() is called.
    setTimeout(() => {
        if (!map || !dotNetRef) return;
        const b = map.getBounds();
        const c = map.getCenter();
        dotNetRef.invokeMethodAsync('OnMapBoundsChanged',
            b.getWest(), b.getSouth(), b.getEast(), b.getNorth(),
            c.lat, c.lng, map.getZoom()).catch(() => { });
    }, 0);
}

/**
 * Batched per-tick frame update. Combines up to six individual
 * interop calls (updatePosition, addColoredTrackPoint, setCourseLine /
 * clearCourseLine, setCurrentArrow, setLaylines) into a single call so
 * the Blazor->JS bridge is only crossed once per SignalK tick instead
 * of 4 to 6 times. On a Raspi kiosk each bridge crossing has measurable
 * overhead (JSON marshal + WASM trampoline + JS invocation), so
 * collapsing them cuts tick-to-paint latency noticeably.
 *
 * `frame` shape (all fields optional where noted):
 *   {
 *     pos: { lat, lon, headingRad, cogRad, sogMs } | null,
 *     track: [lat, lon, sogMs, prevLat, prevLon] | null,
 *     course: { wpLat, wpLon, prevLat, prevLon, xte } | null,
 *     clearCourse: bool,
 *     current: { lat, lon, setRad, driftMs } | null,
 *     laylines: { lat, lon, twdRad, twaRad, wpLat, wpLon } | null
 *   }
 * Any null / missing field is a no-op on that subsystem. Individual
 * export functions (updatePosition etc.) stay exported so anything
 * outside the per-tick hot path can still call them directly.
 */
// Zoom control exports. Blazor draws its own +/- buttons (MapZoomButtons)
// and calls these so the native Leaflet zoom control can stay off. Uses
// map.zoomIn / zoomOut which already respect minZoom / maxZoom, so we
// don't need to clamp here. No-ops before initMap so Blazor can race-
// call without blowing up.
// Default step matches map.zoomDelta (0.5 since the half-step zoom
// landed). Topbar tap, keyboard +/- and wheel all snap to the same
// half-integer levels. Blazor passes a step explicitly when it wants
// 1.0; null / undefined falls through to the map's own delta.
export function zoomIn(step) {
    if (!map) return;
    map.zoomIn(typeof step === 'number' ? step : map.options.zoomDelta);
}
export function zoomOut(step) {
    if (!map) return;
    map.zoomOut(typeof step === 'number' ? step : map.options.zoomDelta);
}

export function applyFrame(frame) {
    if (!map || !frame) return;
    if (frame.pos) {
        const p = frame.pos;
        updatePosition(p.lat, p.lon, p.headingRad, p.cogRad, p.sogMs);
    }
    if (frame.track) {
        const t = frame.track;
        addColoredTrackPoint(t[0], t[1], t[2], t[3], t[4]);
    }
    if (frame.course) {
        const c = frame.course;
        // Reuse the boat position from the frame -- course line needs
        // it and the C# side already sent it, no reason to duplicate.
        const boatLat = frame.pos ? frame.pos.lat : null;
        const boatLon = frame.pos ? frame.pos.lon : null;
        // While editing the active route, suppress the course-line
        // overlay (leg + bearing + XTE tick). The user is actively
        // moving waypoints so the course-line would point at stale
        // geometry; we already cleared the active polyline + next-
        // waypoint marker via setActiveOverlayHidden(true). Re-emerges
        // when the C# side calls setActiveOverlayHidden(false) on
        // edit cancel / save.
        if (boatLat != null && boatLon != null && !activeOverlayHidden) {
            setCourseLine(boatLat, boatLon, c.wpLat, c.wpLon, c.prevLat, c.prevLon, c.xte, c.xteSeverity);
        }
    } else if (frame.clearCourse) {
        clearCourseLine();
    }
    if (frame.current) {
        const cu = frame.current;
        setCurrentArrow(cu.lat, cu.lon, cu.setRad, cu.driftMs);
    }
    if (frame.laylines) {
        const l = frame.laylines;
        setLaylines(l.lat, l.lon, l.twdRad, l.twaRad, l.wpLat, l.wpLon);
    }
}

export function updatePosition(lat, lon, headingRad, cogRad, sogMs) {
    if (!map || !boatMarker) return;

    selfLat = lat; selfLon = lon; selfCogRad = cogRad; selfSogMs = sogMs;

    // Push own-boat state into any active radar overlay so its
    // canvas can be repositioned over the new lat/lon and painted
    // heading-corrected. Falls back to COG when heading is absent;
    // the radar layer itself tolerates undefined.
    setRadarBoatState(lat, lon, headingRad ?? cogRad ?? 0);

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

    if (followBoat && Number.isFinite(lat) && Number.isFinite(lon)) {
        suppressMoveEnd = true;
        // Shift the boat slightly above the geometric viewport centre
        // so more chart area is visible AHEAD of it (the helm's typical
        // task is "what's coming up?" not "where have I been?"). On
        // iPad the top-side HUDs (AWA/AWS, position) and the lower
        // route/depth/anchor cards leave the unobstructed chart area
        // skewed downward, so a literal-centre panTo lands the boat
        // visually too low. 40% from the top works on landscape iPad +
        // desktop without disorienting the helm.
        //
        // Implementation: project the boat to pixel space, push the
        // map centre DOWN in pixels (positive Y) by 10% of the
        // viewport height, then unproject back to a latLng. panTo
        // that latLng so the boat lands at (cx, cy*0.4).
        const z = map.getZoom();
        const size = map.getSize();
        const boatPx = map.project([lat, lon], z);
        const newCenterPx = boatPx.add(L.point(0, size.y * 0.1));
        const newCenter = map.unproject(newCenterPx, z);
        map.panTo(newCenter, { animate: true, duration: 0.5 });
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
        const acColor = inside ? MapColors.anchorOk : MapColors.anchorDrag;
        anchorCircle.setStyle({ color: acColor, fillColor: acColor });
    }

    // Vessel-anchored measurement segments need their geometry + bearing
    // / distance label re-derived from the new own-boat position. Skip
    // the rebuild when no measurement leg references the boat to avoid
    // wasting work on every NMEA tick.
    if (measureActive && measurePoints.length > 0 && hasVesselMeasurePoint()) {
        redrawMeasure();
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

/**
 * Builds the full AIS popup HTML string from a vessel snapshot. Called
 * lazily -- only when the popup is actually about to open or is already
 * open and the data changed. Building 200+ of these every 3 s when the
 * user isn't looking at any of them was visible perf overhead on a
 * weak client.
 *
 * Snapshot shape (stashed on the marker as _onaVesselSnapshot):
 *   { v, selfLat, selfLon, cpaInfo, isDangerEff, isWarning }
 */
function buildAisPopupHtml(snap) {
    const { v, selfLat, selfLon, cpaInfo, isDangerEff, isWarning } = snap;
    const name = esc(v.name || '');
    const mmsi = v.mmsi || '';
    const callsign = v.callsign ? esc(v.callsign) : '';
    const sog = v.sogMs != null ? (v.sogMs * 1.94384).toFixed(1) : '--';
    const cogDeg = v.cogRad != null ? (v.cogRad * DEG).toFixed(0) : '--';
    const hdgDeg = v.headingRad != null ? (v.headingRad * DEG).toFixed(0) : '--';
    const type = v.shipType ? esc(v.shipType) : '';
    const dist = haversineMeters(selfLat, selfLon, v.lat, v.lon) * NM_PER_METER;
    const brg = bearingDeg(selfLat, selfLon, v.lat, v.lon);

    // Display name preference: SignalK name -> external-lookup cache
    // -> callsign -> MMSI.
    let displayTitle;
    const cachedName = mmsi ? vesselNameCacheGet(mmsi) : undefined;
    if (name)            displayTitle = name;
    else if (cachedName) displayTitle = esc(cachedName);
    else if (callsign)   displayTitle = callsign;
    else if (mmsi)       displayTitle = `MMSI ${esc(mmsi)}`;
    else                 displayTitle = 'Unknown';
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
    // delegated handler on mapEl can route both to Blazor without
    // leaking a callback through string concatenation.
    const buddyLabel = v.buddy ? '\u2605 Remove buddy' : '\u2606 Add buddy';
    const buddyAttrs = `data-ona-buddy="1" data-ctx="${esc(v.context)}" data-mmsi="${esc(mmsi || '')}"`
        + ` data-nm="${esc(v.name || '')}" data-is="${v.buddy ? '1' : '0'}"`;
    const showSnooze = !v.buddy && (isDangerEff || isWarning);
    const snoozeAttrs = `data-ona-snooze="1" data-ctx="${esc(v.context)}" data-nm="${esc(v.name || mmsi || '')}"`;
    const snoozeHtml = showSnooze
        ? `<a href="#" ${snoozeAttrs} style="color:#fbbf24;font-size:11px;text-decoration:none">\u266B Snooze alarm</a>`
        : '';

    // Two action rows. Row 1: external-lookup links (MarineTraffic +
    // VesselFinder) side-by-side on a single flex line -- they're the
    // primary "tell me more about this vessel" action. Row 2: local
    // actions (Buddy toggle + Snooze alarm) since they mutate app
    // state and belong together. Splitting the rows stops the local
    // actions from wrapping between the two external links on narrow
    // popups and groups them by intent.
    let linksHtml = '';
    if (mmsi || showSnooze) {
        const linkStyle = 'color:#7dd3fc;font-size:11px;text-decoration:none;flex:1;text-align:center;padding:2px 4px;white-space:nowrap';
        const rows = [];
        if (mmsi) {
            rows.push(
                `<div style="display:flex;gap:10px;align-items:center">` +
                `<a href="${mtUrl}" target="_blank" rel="noopener" style="${linkStyle}">MarineTraffic</a>` +
                `<a href="${vfUrl}" target="_blank" rel="noopener" style="${linkStyle}">VesselFinder</a>` +
                `</div>`
            );
        }
        const row2 = [];
        if (mmsi) {
            row2.push(`<a href="#" ${buddyAttrs} style="color:#facc15;font-size:11px;text-decoration:none">${buddyLabel}</a>`);
        }
        if (snoozeHtml) row2.push(snoozeHtml);
        if (row2.length > 0) {
            rows.push(`<div style="display:flex;gap:12px;flex-wrap:wrap">${row2.join('')}</div>`);
        }
        linksHtml = `<div style="margin-top:6px;padding-top:6px;border-top:1px solid rgba(255,255,255,0.08);display:flex;flex-direction:column;gap:6px">` +
            rows.join('') + `</div>`;
    }

    // Country flag from signalk-flags plugin. 404s on servers without
    // the plugin trigger onerror + hide; no broken-image glyph.
    const flagHtml = mmsi
        ? `<img class="ais-popup-flag" src="${flagUrl(mmsi)}" alt="" onerror="this.style.display='none'">`
        : '';

    return (
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
        `</div>`
    );
}

export function updateAisTargets(vessels) {
    if (!map) return;
    // During an active drag a full AIS rebuild is the largest per-frame
    // cost in the module (200+ markers, CPA overlays, trails, popups).
    // Defer until the user lets go; the next 3 s tick picks up any
    // changes that happened during the drag. Safety is preserved
    // because alarms run on the C# side off the delta stream, not
    // off the JS marker state.
    if (isSlowClient && map.dragging && map.dragging._moving) return;
    const seen = new Set();

    for (const v of vessels) {
        seen.add(v.context);
        if (v.lat == null || v.lon == null || !isFinite(v.lat) || !isFinite(v.lon)) continue;

        // Pre-fetch the country flag on first sight so the AIS popup
        // doesn't flash while it loads the SVG on first click.
        if (v.mmsi) prewarmFlag(v.mmsi);

        // CPA + TCPA come pre-computed from the C# side (Utilities/Cpa)
        // so the map marker path and the Layers-panel list can't disagree.
        // Shape the expected record for the rest of the loop.
        const cpaInfo = (v.cpaNm != null && v.tcpaMin != null)
            ? { cpa: v.cpaNm, tcpa: v.tcpaMin }
            : null;
        // CPA threat band is also computed C#-side (Cpa.ClassifyThreat)
        // using the helm's guard-zone radius / lookahead / warning factor.
        // Three buckets: "danger" (red ring + red crossing line),
        // "warning" (amber crossing line, advisory), "none" (no overlay).
        // Buddies are exempted on the C# side so we don't re-check here.
        const isDangerEff = v.cpaThreat === 'danger';
        const isWarning   = v.cpaThreat === 'warning';
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
            color = isDangerEff ? MapColors.danger : MapColors.radar;
        } else if (v.buddy) {
            color = MapColors.buddy;              // buddies always win
        } else if (isDangerEff) {
            color = MapColors.danger;             // CPA alarm active
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
            // During route / polygon / measurement edit, a tap on a vessel
            // should behave like a tap on empty water: append a waypoint,
            // not open the vessel popup. Without this guard the click
            // reaches the marker first (Leaflet's default binding), the
            // popup shows, and the route never picks up the point. We
            // attach the guard on FIRST CREATE so the once-per-marker
            // cost is trivial even in 200-vessel harbours.
            marker.on('click', (ev) => {
                if (routeEditMode || polygonEditMode || measureActive) {
                    L.DomEvent.stopPropagation(ev);
                    L.DomEvent.preventDefault(ev);
                    const ll = ev.latlng || marker.getLatLng();
                    if (routeEditMode)         addEditWaypoint(ll.lat, ll.lng);
                    else if (polygonEditMode)  addPolygonVertexInternal(ll.lat, ll.lng);
                    else                       addMeasurePoint(ll.lat, ll.lng);
                    marker.closePopup();
                }
            });
        } else {
            marker.setLatLng([v.lat, v.lon]);
            marker.setIcon(icon);
        }
        if (!isSart) rotateMarker(marker, v.cogRad ?? v.headingRad);

        // Pulse an expanding red ring around any AIS / radar target
        // whose CPA is in the "danger" band (matches the MapColors.danger
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

        // Name label visible at zoom >= 12. Resolution (name -> mmsi,
        // with buddy star prefix) happens C#-side -- Map.razor.PushAisTargets
        // stamps v.displayName so this label and any other label-rendering
        // surface share one fallback chain. Suppressed in harbor mode
        // to keep the chart legible when entering a busy port.
        const displayName = v.displayName || null;
        if (displayName && !harborMode) {
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
        // Building the HTML for every vessel every tick (200+ in a busy
        // harbour, 3 s cadence) shows up in profiles as measurable
        // overhead even though most popups are never opened. We now
        // STASH the snapshot on the marker and only rebuild when the
        // popup is actually visible -- once on popupopen and again on
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
            // autoPan: false -- a CPA banner often prompts the helm to
            // tap the threatening AIS marker to investigate, and the
            // default Leaflet popup auto-pan would shift the map away
            // from own boat to fit the popup. The helm wanted "no
            // focus change on collision course" -- they want to see
            // own boat AND the threat geometry, not have the chart
            // jerk to keep a popup on screen. Helm can still pan
            // manually.
            marker.bindPopup('',
                { closeButton: false, maxWidth: 420, className: 'ais-popup', autoPan: false });
            marker.on('popupopen', () => {
                if (marker._onaVesselSnapshot) {
                    marker.setPopupContent(buildAisPopupHtml(marker._onaVesselSnapshot));
                }
            });
        } else if (marker.isPopupOpen()) {
            // Popup is on screen right now -- user is watching. Refresh
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

        // Trail: last AIS_TRAIL_SECONDS of positions, drawn as a fading line.
        // We only push when the position actually changes to avoid empty ticks.
        updateAisTrail(v.context, v.lat, v.lon);

        // Course vector. Suppressed in harbor mode -- with dozens of
        // AIS targets in port every vector sweeps across every other
        // marker and the chart turns into a hatch of dashed lines.
        const end = vectorEnd(v.lat, v.lon, v.cogRad, v.sogMs);
        if (end && !harborMode) {
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
        // and the red lines would be misleading. Suppressed in harbor
        // mode where every other vessel is technically a "near miss" --
        // the audio CPA alarm is suppressed in C# (CpaAlarmRule short-
        // circuits on Settings.HarborMode) and the on-chart overlay
        // would just add noise to a chart the helm needs to read.
        if ((isDangerEff || isWarning) && cpaInfo && cpaInfo.tcpa > 0 && !harborMode) {
            const tcpaSec = cpaInfo.tcpa * 60;
            const ownCpa = destPoint(selfLat, selfLon, selfCogRad, selfSogMs * tcpaSec);
            const tgtCpa = destPoint(v.lat, v.lon, v.cogRad, v.sogMs * tcpaSec);
            const lineColor = isDangerEff ? MapColors.mob : MapColors.guardWarn;

            updateCpaLine(aisCpaOwnLines, v.context, [selfLat, selfLon], ownCpa, lineColor);
            updateCpaLine(aisCpaTgtLines, v.context, [v.lat, v.lon], tgtCpa, lineColor);

            const midLat = (ownCpa[0] + tgtCpa[0]) / 2;
            const midLon = (ownCpa[1] + tgtCpa[1]) / 2;
            // Label now leads with the target name (or MMSI fallback) so a
            // sailor glancing at the chart knows WHICH vessel is on a
            // collision track without having to click the marker. Format:
            //   MV Aurora
            //   0.42 nm · T-5m
            // displayName is the C#-resolved fallback chain (name ->
            // mmsi -> short context); falls back to v.name / v.mmsi
            // explicitly here so the label still renders something
            // useful if the C# pipeline missed a tick.
            const cpaName = v.displayName || v.name || v.mmsi || 'Unknown';
            const labelText = `<strong>${esc(cpaName)}</strong><br>${cpaInfo.cpa.toFixed(2)} nm · T-${cpaInfo.tcpa.toFixed(0)}m`;
            let lbl = aisCpaLabels[v.context];
            if (!lbl) {
                // `interactive: true` lets the label accept pointer events
                // (clicks + touch taps). Without it Leaflet routes every
                // event on the tooltip surface straight to the map below,
                // which is why tapping the CPA chip on the chart previously
                // did nothing. Paired with the click handler below, a tap
                // now opens the target vessel's full popup so the helm
                // can read name / MMSI / SOG / COG / COLREGS role without
                // having to hunt the tiny triangle marker.
                lbl = L.tooltip({
                    permanent: true, direction: 'center', interactive: true,
                    className: `cpa-label ${isDangerEff ? 'cpa-danger' : 'cpa-warn'}`
                }).setLatLng([midLat, midLon]).setContent(labelText).addTo(map);
                aisCpaLabels[v.context] = lbl;
                attachCpaLabelClick(lbl, v.context);
            } else {
                lbl.setLatLng([midLat, midLon]);
                lbl.setContent(labelText);
                const el = lbl.getElement();
                if (el) {
                    el.classList.toggle('cpa-danger', isDangerEff);
                    el.classList.toggle('cpa-warn', !isDangerEff);
                    // setContent rebuilds the DOM, so the click listener
                    // we attached via addEventListener survives on the
                    // container but not if the container itself was
                    // replaced. Re-wiring every update is cheap and
                    // covers the replacement case without bookkeeping.
                    attachCpaLabelClick(lbl, v.context);
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
 * Wires a click / tap on a CPA label to open the target vessel's
 * popup. The popup (built by buildAisPopupHtml) already carries full
 * detail: name, MMSI, callsign, SOG, COG, HDG, bearing / distance
 * from own boat, COLREGS role, external-lookup links, Buddy / Snooze
 * controls, AND a CPA row. The compact CPA label on the chart is a
 * glance-level chip; the full popup is the one-tap drill-in.
 *
 * `stopPropagation` keeps the map from panning or the popup under
 * the label from opening instead. L.DomEvent handles both mouse and
 * touch paths so iPad taps work the same as desktop clicks.
 */
function attachCpaLabelClick(tooltip, vesselContext) {
    const el = tooltip.getElement();
    if (!el) return;
    if (el._onaCpaClickBound) return; // idempotent for setContent rebuilds
    el._onaCpaClickBound = true;
    L.DomEvent.on(el, 'click touchend', (e) => {
        L.DomEvent.stop(e);
        const marker = aisMarkers[vesselContext];
        if (marker && typeof marker.openPopup === 'function') {
            marker.openPopup();
        }
    });
}

/**
 * Updates the collision-avoidance thresholds used to colour AIS targets and
 * draw crossing-situation lines. Also resizes the guard zone ring.
 * @param radiusNm    CPA threshold (nautical miles)
 * @param lookaheadMin  TCPA threshold (minutes)
 */
/** Pans the map to an AIS vessel and opens its popup. Returns true
 *  when a marker existed; false when the context didn't match anything
 *  (vessel aged out, AIS filter hiding it, deleted since the list
 *  rendered). The C# caller uses the return value to toast + restore
 *  follow so the user isn't left wondering why the tap did nothing. */
export function focusVessel(context) {
    const marker = aisMarkers[context];
    if (!marker || !map) return false;
    const ll = marker.getLatLng();
    map.panTo(ll, { animate: true });
    marker.openPopup();
    return true;
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

// Harbor-mode flag. When true the AIS render path skips name labels,
// COG vectors, and CPA overlays, the guard-zone ring is not drawn,
// and moored vessels are filtered upstream in C#. setHarborMode also
// tears down any in-flight overlays so the helm sees the declutter
// take effect immediately, not after the next AIS push tick.
let harborMode = false;
// Helper: best-effort layer removal that never throws. Some entries in
// the per-context dicts can be null / undefined under tear-down races
// (a concurrent updateAisTargets that just deleted the key, or a
// disposed Leaflet layer); without the guard map.removeLayer(undefined)
// throws TypeError: Cannot read properties of undefined ('_layerAdd')
// and the whole setHarborMode call rejects -- which the C# side then
// has to roll back via the toast path (see Map.razor.ToggleHarborMode).
// Catching here makes the JS-side teardown best-effort and lets the
// C# happy path stay green.
function _safeRemoveLayer(layer) {
    if (!layer || !map) return;
    try { map.removeLayer(layer); } catch (_) { /* already gone */ }
}
export function setHarborMode(enabled) {
    harborMode = !!enabled;
    if (!map) return;
    if (harborMode) {
        for (const ctx of Object.keys(aisLabels)) {
            try { aisMarkers[ctx]?.unbindTooltip(); } catch (_) { /* marker gone */ }
            delete aisLabels[ctx];
        }
        for (const ctx of Object.keys(aisVectors)) {
            _safeRemoveLayer(aisVectors[ctx]);
            delete aisVectors[ctx];
        }
        for (const ctx of Object.keys(aisCpaOwnLines)) {
            _safeRemoveLayer(aisCpaOwnLines[ctx]);
            delete aisCpaOwnLines[ctx];
        }
        for (const ctx of Object.keys(aisCpaTgtLines)) {
            _safeRemoveLayer(aisCpaTgtLines[ctx]);
            delete aisCpaTgtLines[ctx];
        }
        for (const ctx of Object.keys(aisCpaLabels)) {
            _safeRemoveLayer(aisCpaLabels[ctx]);
            delete aisCpaLabels[ctx];
        }
        if (guardZoneRing) {
            _safeRemoveLayer(guardZoneRing);
            guardZoneRing = null;
        }
    } else {
        // Coming out of harbor mode: redraw the guard ring at the
        // current radius. AIS labels / vectors / CPA overlays will
        // be re-established by the next updateAisTargets tick.
        drawGuardZone();
    }
}

function drawGuardZone() {
    if (!map) return;
    // Harbor mode hides the ring entirely. The radius itself is not
    // touched (so leaving harbor mode restores the previous setting).
    if (harborMode) {
        if (guardZoneRing) { map.removeLayer(guardZoneRing); guardZoneRing = null; }
        return;
    }
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
            color: MapColors.guardWarn,
            weight: 1,
            opacity: 0.5,
            fillColor: MapColors.guardWarn,
            fillOpacity: 0.04,
            // Non-interactive: the ring no longer gets its own tooltip
            // (user-reported: the "Guard zone (CPA alarm radius)"
            // hover chip was distracting). The legend + Settings
            // already explain what the amber ring is; we don't need
            // to repeat it on hover. Non-interactive also avoids the
            // ring stealing pointer events from anything under it.
            interactive: false,
        }).addTo(map);
    } else {
        guardZoneRing.setLatLng([selfLat, selfLon]);
        guardZoneRing.setRadius(radiusM);
    }
}

// --- Persistent measurement tool ---
// Multi-segment ruler: clicks drop measurement points, each segment is
// labelled with bearing + distance and the last point shows a running
// total. A point can be vessel-anchored (tracks own-boat as it moves)
// or fixed on the chart, which lets the helm answer both "how far is
// that island from where I am?" and "how long is this planned leg?"
// with the same tool. The colour matches what used to be the
// double-click "bearing line" so the two overlays read as one feature.
//
// Editing model mirrors Route Edit so the helm doesn't have to learn a
// new gesture vocabulary:
//   * Drag a fixed point to move it (vessel-anchored points are
//     non-draggable since they track own-boat live).
//   * Tap a segment to insert a new point at the click location.
//   * Right-click / long-press anywhere clears the ruler without
//     leaving Measure mode.
const MEASURE_COLOR = '#e2e8f0';
let measureActive = false;
let measurePoints = [];          // [{ lat, lon, vessel: bool }, ...]
let measureMarkers = [];         // L.marker[]   parallel to measurePoints
let measureSegments = [];        // L.polyline[] one per segment between points
let measureHitLines = [];        // L.polyline[] thick invisible per-segment hitbox
let measureTooltips = [];        // L.tooltip[]  one per segment, anchored at end
let measureSuppressNextMapClick = false;

export function setMeasureMode(active) {
    measureActive = !!active;
    if (!measureActive) clearMeasure();
    if (map) {
        // Visual hint: crosshair cursor when in measurement mode.
        map.getContainer().style.cursor = measureActive ? 'crosshair' : '';
    }
}

function removeMeasureLayers() {
    if (!map) {
        measureMarkers = []; measureSegments = []; measureHitLines = []; measureTooltips = [];
        return;
    }
    for (const m of measureMarkers) map.removeLayer(m);
    for (const s of measureSegments) map.removeLayer(s);
    for (const h of measureHitLines) map.removeLayer(h);
    for (const t of measureTooltips) map.removeLayer(t);
    measureMarkers = [];
    measureSegments = [];
    measureHitLines = [];
    measureTooltips = [];
}

export function clearMeasure() {
    removeMeasureLayers();
    measurePoints = [];
}

// Returns the current chart position for a measurement point. Vessel-
// anchored points read live own-boat coords so the segment they
// participate in updates as the boat moves.
function measurePointLatLng(p) {
    return p.vessel ? [selfLat, selfLon] : [p.lat, p.lon];
}

function hasVesselMeasurePoint() {
    for (const p of measurePoints) if (p.vessel) return true;
    return false;
}

function addMeasurePoint(lat, lon) {
    if (!map) return;
    measurePoints.push({ lat, lon, vessel: false });
    redrawMeasure();
}

// Adds a vessel-anchored measurement point. Used by the boat-marker
// click handler in measure mode and by measureFromVesselTo() below.
function addVesselMeasurePoint() {
    if (!map) return;
    measurePoints.push({ lat: selfLat, lon: selfLon, vessel: true });
    redrawMeasure();
}

function makeMeasureDotIcon(vessel) {
    return L.divIcon({
        // The default leaflet-div-icon styling adds a white background +
        // black border; .ona-measure-marker neuters both so the inner
        // span fully owns the visual.
        className: 'ona-measure-marker',
        html: vessel
            ? '<div class="ona-measure-dot ona-measure-dot-vessel"></div>'
            : '<div class="ona-measure-dot"></div>',
        iconSize: [14, 14],
        iconAnchor: [7, 7]
    });
}

// Drag handlers that mirror bindEditMarker for routes: an original-
// position ghost + dashed delta line + tooltip showing how far the
// point has moved. Reuses .measure-tooltip styling so the look stays
// consistent with the running-total label on each segment.
function bindMeasureMarker(marker, idx) {
    let ghostLine = null;
    let ghostMarker = null;
    let origLL = null;

    marker.on('dragstart', (e) => {
        origLL = e.target.getLatLng();
        ghostMarker = L.marker(origLL, {
            icon: L.divIcon({
                className: 'ona-measure-marker',
                html: '<div class="ona-measure-ghost"></div>',
                iconSize: [14, 14],
                iconAnchor: [7, 7]
            }),
            interactive: false, keyboard: false, zIndexOffset: 500
        }).addTo(map);
        ghostLine = L.polyline([origLL, origLL], {
            color: MEASURE_COLOR, weight: 1.5, opacity: 0.7, dashArray: '3,4',
            interactive: false
        }).addTo(map);
        ghostLine.bindTooltip('Δ 0 m', {
            permanent: true, direction: 'center', className: 'measure-tooltip'
        }).openTooltip(origLL);
    });

    marker.on('drag', (e) => {
        const ll = e.target.getLatLng();
        if (idx < 0 || idx >= measurePoints.length) return;
        measurePoints[idx].lat = ll.lat;
        measurePoints[idx].lon = ll.lng;
        // Live update of just the segments adjacent to this marker --
        // a full redraw would tear down the marker mid-drag and break
        // Leaflet's drag tracking.
        updateMeasureSegmentsAround(idx);
        if (ghostLine && origLL) {
            ghostLine.setLatLngs([origLL, ll]);
            const dm = haversineMeters(origLL.lat, origLL.lng, ll.lat, ll.lng);
            const label = dm < 1000 ? `Δ ${dm.toFixed(0)} m`
                                    : `Δ ${(dm * NM_PER_METER).toFixed(2)} nm`;
            ghostLine.setTooltipContent(label);
            const tt = ghostLine.getTooltip();
            if (tt) tt.setLatLng(ll);
        }
    });

    marker.on('dragend', () => {
        if (ghostMarker && map) map.removeLayer(ghostMarker);
        if (ghostLine && map) map.removeLayer(ghostLine);
        ghostMarker = null; ghostLine = null; origLL = null;
        // Final canonical redraw so running totals on every tooltip
        // reflect the new geometry.
        redrawMeasure();
    });
}

// Update the geometry + tooltips of segments that touch point `idx`,
// plus refresh every later tooltip's running total. Used during drag
// where a full tear-down would interrupt Leaflet's drag tracking.
function updateMeasureSegmentsAround(idx) {
    if (!map || measurePoints.length < 2) return;
    const positions = measurePoints.map(measurePointLatLng);

    // Recompute the running total once and walk segments updating
    // both the visible polyline geometry and each tooltip's content
    // (only the affected ones strictly need geometry, but content
    // depends on the running total which shifts when any earlier
    // segment changed length).
    let runningNm = 0;
    for (let i = 1; i < positions.length; i++) {
        const a = positions[i - 1];
        const b = positions[i];
        const segDist = haversineMeters(a[0], a[1], b[0], b[1]) * NM_PER_METER;
        const segBrg = bearingDeg(a[0], a[1], b[0], b[1]);
        runningNm += segDist;

        const segLayer = measureSegments[i - 1];
        const hitLayer = measureHitLines[i - 1];
        const tipLayer = measureTooltips[i - 1];
        if (segLayer) segLayer.setLatLngs([a, b]);
        if (hitLayer) hitLayer.setLatLngs([a, b]);
        if (tipLayer) {
            tipLayer.setLatLng(b);
            tipLayer.setContent(`${segBrg.toFixed(0)}&deg; / ${segDist.toFixed(2)} nm<br/>total ${runningNm.toFixed(2)} nm`);
        }
    }
}

// Find the segment closest to `ll` and splice a new fixed point in at
// that position. Mirrors insertEditVertexOnSegment for routes; the
// suppress flag stops the trailing map-click from appending a phantom
// duplicate point at the end of the ruler.
function insertMeasurePointOnSegment(ll) {
    if (!map || measurePoints.length < 2) return;
    const positions = measurePoints.map(measurePointLatLng);
    const p = map.latLngToLayerPoint(ll);
    let bestIdx = 0;
    let bestDist = Infinity;
    for (let i = 0; i < positions.length - 1; i++) {
        const a = map.latLngToLayerPoint(L.latLng(positions[i][0], positions[i][1]));
        const b = map.latLngToLayerPoint(L.latLng(positions[i + 1][0], positions[i + 1][1]));
        const d = pointToSegmentPixels(p, a, b);
        if (d < bestDist) { bestDist = d; bestIdx = i; }
    }
    const insertAt = bestIdx + 1;
    measurePoints.splice(insertAt, 0, { lat: ll.lat, lon: ll.lng, vessel: false });
    measureSuppressNextMapClick = true;
    redrawMeasure();
}

// Tear down and rebuild every measure layer from the points array.
// A redraw is simpler than tracking per-segment layer references and
// avoids drift when points are added or cleared mid-update; the
// updateMeasureSegmentsAround() helper above is the partial-redraw
// path we take during a drag where we MUST keep the marker alive.
function redrawMeasure() {
    if (!map) return;
    removeMeasureLayers();
    if (measurePoints.length === 0) return;

    const positions = measurePoints.map(measurePointLatLng);

    // Pre-compute running totals so the last-point tooltip can show
    // the cumulative distance without re-walking the array each tick.
    let runningTotalNm = 0;
    for (let i = 0; i < positions.length; i++) {
        const [lat, lon] = positions[i];
        const point = measurePoints[i];

        const marker = L.marker([lat, lon], {
            icon: makeMeasureDotIcon(point.vessel),
            // Vessel-anchored points track own-boat live; allowing a
            // drag here would silently fight the next position update.
            // Only fixed points are draggable.
            draggable: !point.vessel,
            zIndexOffset: 800,
        }).addTo(map);
        if (!point.vessel) bindMeasureMarker(marker, i);
        measureMarkers.push(marker);

        if (i >= 1) {
            const a = positions[i - 1];
            const b = positions[i];
            const segDist = haversineMeters(a[0], a[1], b[0], b[1]) * NM_PER_METER;
            const segBrg = bearingDeg(a[0], a[1], b[0], b[1]);
            runningTotalNm += segDist;

            // Visible dashed segment.
            const seg = L.polyline([a, b], {
                color: MEASURE_COLOR, weight: 2, dashArray: '6,4', opacity: 0.85,
                className: 'ona-measure-line',
            }).addTo(map);
            seg.on('click', (e) => {
                L.DomEvent.stopPropagation(e);
                insertMeasurePointOnSegment(e.latlng);
            });
            measureSegments.push(seg);

            // Wider invisible hit line so a fingertip-width tap
            // anywhere near the segment splits it. 24 px matches what
            // route edit uses (40 px there; measure is more transient
            // so a tighter band keeps accidental inserts down).
            const hit = L.polyline([a, b], {
                color: MEASURE_COLOR, weight: 24, opacity: 0,
                interactive: true, className: 'ona-measure-hit-line',
            }).addTo(map);
            hit.on('click', (e) => {
                L.DomEvent.stopPropagation(e);
                insertMeasurePointOnSegment(e.latlng);
            });
            measureHitLines.push(hit);

            const tooltip = L.tooltip({
                permanent: true, direction: 'right', offset: [8, 0],
                className: 'measure-tooltip'
            })
                .setLatLng(b)
                .setContent(`${segBrg.toFixed(0)}&deg; / ${segDist.toFixed(2)} nm<br/>total ${runningTotalNm.toFixed(2)} nm`)
                .addTo(map);
            measureTooltips.push(tooltip);
        }
    }
}

// Public entry point for "Measure Here" in the map context menu.
// Drops a fresh two-point measurement: vessel as the moving anchor,
// the clicked spot as the fixed endpoint. Activates measure mode so
// the helm can keep tapping to extend the ruler if they want a
// multi-leg distance.
export function measureFromVesselTo(lat, lon) {
    if (!map) return;
    clearMeasure();
    measureActive = true;
    map.getContainer().style.cursor = 'crosshair';
    addVesselMeasurePoint();
    addMeasurePoint(lat, lon);
}

// --- MOB ---

export function setMob(lat, lon) {
    if (!map) return;  // page unmounted mid-dispatch; same guard as setAnchor.
    clearMob();
    mobMarker = L.marker([lat, lon], { icon: mobIcon, zIndexOffset: 2000 }).addTo(map);
    mobCircle = L.circle([lat, lon], { radius: 50, color: MapColors.mob, fillColor: MapColors.mob,
        fillOpacity: 0.15, weight: 2 }).addTo(map);
    mobLine = L.polyline([[selfLat, selfLon], [lat, lon]], {
        color: MapColors.mob, weight: 2, dashArray: '4,4'
    }).addTo(map);
    mobLabel = L.tooltip({ permanent: true, direction: 'center', className: 'mob-tooltip' })
        .setLatLng([(selfLat + lat)/2, (selfLon + lon)/2])
        .setContent('MOB')
        .addTo(map);
    // Audible confirmation: the helm may have been looking overboard
    // when they pressed the button and can't see the pulse animation.
    // Two-tone chime (880/660 Hz, same palette as the connection
    // alarm, but once-only). Inline AudioContext so setMob doesn't
    // depend on MainLayout's module reference; AudioContext is cheap
    // to spin up and is garbage-collected when this scope ends.
    try { playMobChime(); } catch (_) { /* audio blocked in context */ }
}

function playMobChime() {
    const Ctx = window.AudioContext || window.webkitAudioContext;
    if (!Ctx) return;
    const ctx = new Ctx();
    if (ctx.state === 'suspended') ctx.resume();
    const beep = (freq, atSec, durMs) => {
        const osc = ctx.createOscillator();
        const gain = ctx.createGain();
        osc.connect(gain); gain.connect(ctx.destination);
        osc.type = 'square';
        osc.frequency.value = freq;
        gain.gain.setValueAtTime(0.18, ctx.currentTime + atSec);
        gain.gain.exponentialRampToValueAtTime(0.001, ctx.currentTime + atSec + durMs / 1000);
        osc.start(ctx.currentTime + atSec);
        osc.stop(ctx.currentTime + atSec + durMs / 1000);
    };
    beep(880, 0,    220);
    beep(660, 0.26, 220);
    beep(880, 0.54, 260);
    // Close the context shortly after the last note so the ~1s lifetime
    // doesn't linger. Safari occasionally warns about >6 live contexts.
    setTimeout(() => { try { ctx.close(); } catch (_) {} }, 1200);
}

export function clearMob() {
    if (!map) { mobMarker = null; mobCircle = null; mobLine = null; mobLabel = null; return; }
    if (mobMarker) { map.removeLayer(mobMarker); mobMarker = null; }
    if (mobCircle) { map.removeLayer(mobCircle); mobCircle = null; }
    if (mobLine) { map.removeLayer(mobLine); mobLine = null; }
    if (mobLabel) { map.removeLayer(mobLabel); mobLabel = null; }
}

// --- Anchor Watch ---

export function setAnchor(lat, lon, radiusM) {
    // Swallow calls that hit after the Map page unmounted. Blazor's
    // OnDataChanged handler dispatches asynchronously, so an in-flight
    // HandleDataChanged can land here after DisposeAsync -> dispose()
    // already nulled `map`. Without this guard the next `addTo(map)`
    // throws "can't access property addLayer, t is null" through the
    // console every time the user switches from Map to Dashboard.
    if (!map) return;
    clearAnchor();
    anchorMarker = L.circleMarker([lat, lon], {
        radius: 5, color: MapColors.anchorOk, fillColor: MapColors.anchorOk, fillOpacity: 1
    }).addTo(map);
    anchorCircle = L.circle([lat, lon], {
        radius: radiusM, color: MapColors.anchorOk, fillColor: MapColors.anchorOk,
        fillOpacity: 0.06, weight: 2, dashArray: '6,4'
    }).addTo(map);
    // Seed the trail with the current boat position so the first segment
    // renders without waiting for ANCHOR_TRAIL_SAMPLE_MS.
    if (selfLat && selfLon) anchorTrail.push({ lat: selfLat, lon: selfLon, t: Date.now() });
    // Radius line + label from boat to the anchor dot. Gives the
    // helm a direct visual "you're X metres from the pin" that the
    // watch circle alone doesn't -- the circle shows the permitted
    // swing, not the current offset.
    redrawAnchorRadiusOverlay(lat, lon, radiusM);
}

export function clearAnchor() {
    if (!map) { anchorMarker = null; anchorCircle = null; anchorTrailLayer = null; anchorTrail.length = 0; anchorRadiusLine = null; return; }
    if (anchorMarker) { map.removeLayer(anchorMarker); anchorMarker = null; }
    if (anchorCircle) { map.removeLayer(anchorCircle); anchorCircle = null; }
    if (anchorTrailLayer) { map.removeLayer(anchorTrailLayer); anchorTrailLayer = null; }
    if (anchorRadiusLine) { map.removeLayer(anchorRadiusLine); anchorRadiusLine = null; }
    anchorTrail.length = 0;
}

// Visually mark the anchor as "raising" while we wait for the server's
// cleared-anchor delta to land. Dims the marker + watch-circle + radius
// line so the helm sees the action took effect without us optimistically
// hiding the marker (which would mask a server-side raise failure and
// race the next SyncServerAnchorAsync tick). The setStyle calls fall
// through to no-op when a layer is null, so it's safe to call before
// or after setAnchor / clearAnchor.
export function setAnchorRaising(raising) {
    if (!map) return;
    if (anchorMarker) {
        anchorMarker.setStyle(raising
            ? { opacity: 0.35, fillOpacity: 0.4 }
            : { opacity: 1.0, fillOpacity: 1.0 });
    }
    if (anchorCircle) {
        anchorCircle.setStyle(raising
            ? { opacity: 0.35, fillOpacity: 0.02, dashArray: '4,6' }
            : { opacity: 1.0, fillOpacity: 0.06, dashArray: '6,4' });
    }
    if (anchorRadiusLine) {
        anchorRadiusLine.setStyle(raising
            ? { opacity: 0.3 }
            : { opacity: 0.7 });
    }
}

export function updateAnchorRadius(radiusM) {
    if (anchorCircle) anchorCircle.setRadius(radiusM);
    if (anchorMarker) {
        const ll = anchorMarker.getLatLng();
        redrawAnchorRadiusOverlay(ll.lat, ll.lng, radiusM);
    }
}

// Draws the boat<->anchor line + midpoint "Xm" label. Re-entrant:
// callable on every position update to keep the line pinned while
// the boat drifts on its swing. Falls back to a "waiting for fix"
// stub when the plotter hasn't seen a self-position yet.
// Anchor radius overlay: dashed line from boat to anchor.
// Mutate-in-place via setLatLngs so the 1 Hz position update doesn't
// rebuild the SVG path each tick. Guards against missing fix and
// against NaN sensor glitches (a divide-by-zero upstream would
// otherwise leave the polyline in an invalid state and break
// subsequent setLatLngs calls).
function redrawAnchorRadiusOverlay(anchorLat, anchorLon, _radiusM) {
    if (!map) return;
    if (!Number.isFinite(selfLat) || !Number.isFinite(selfLon)
        || !Number.isFinite(anchorLat) || !Number.isFinite(anchorLon)) {
        if (anchorRadiusLine) { map.removeLayer(anchorRadiusLine); anchorRadiusLine = null; }
        return;
    }

    if (!anchorRadiusLine) {
        anchorRadiusLine = L.polyline(
            [[selfLat, selfLon], [anchorLat, anchorLon]],
            { color: MapColors.anchorOk, weight: 1.5, opacity: 0.7, dashArray: '4,3', interactive: false }
        ).addTo(map);
    } else {
        anchorRadiusLine.setLatLngs([[selfLat, selfLon], [anchorLat, anchorLon]]);
    }
}

function updateAnchorTrail(lat, lon) {
    if (!map) return;  // page unmounted; skip rather than dereference a null map.
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

    // Keep the radius line + label chasing the boat as it drifts.
    // The anchor position itself is static (set once) but the label
    // shows current offset, so it needs a re-render each update.
    if (anchorMarker) {
        const a = anchorMarker.getLatLng();
        const r = anchorCircle ? anchorCircle.getRadius() : 0;
        redrawAnchorRadiusOverlay(a.lat, a.lng, r);
    }

    if (anchorTrail.length < 2) return;
    const coords = anchorTrail.map(p => [p.lat, p.lon]);
    if (!anchorTrailLayer) {
        anchorTrailLayer = L.polyline(coords, {
            color: MapColors.anchorOk, weight: 2, opacity: 0.55,
            dashArray: '2,4', interactive: false
        }).addTo(map);
    } else {
        anchorTrailLayer.setLatLngs(coords);
    }
}

// --- Radar spoke overlay ---
// Thin re-exports so Blazor's JSObjectReference can call the
// enable/disable functions on this module (its existing handle).
// setRadarBoatState is called from inside updateBoatPosition below.

/**
 * @param {object} cfg  { radarId, spokeDataUrl, spokesPerRevolution,
 *                        maxSpokeLength, range, legend?, opacity? }
 */
export function startRadarOverlay(cfg) {
    if (!map) return;
    enableRadarOverlay({ map }, cfg);
}

export function stopRadarOverlay(radarId) {
    disableRadarOverlay(radarId);
}

export function updateRadarRange(radarId, range) {
    setRadarRange(radarId, range);
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
//
// Overzoom was removed: no probe, no tile-error calibrator, no
// cross-layer maxZoom race. Each chart simply gets maxZoom set to its
// declared maxZoom. When a chart's metadata lies (declared z18 but
// server only has z15), tiles above the real max 404 and the base
// layer shows through. Honest but simple; the feature is on the
// backlog to revisit.
export function addChartLayer(id, tileUrl, minZoom, maxZoom, opacity, bounds) {
    if (!map || chartLayers.has(id)) return false;
    // OSM + OpenSeaMap stay attached as permanent fallback layers
    // even when SignalK chart-tiles are active:
    //   * OpenStreetMap road tiles fill anywhere SK chart tiles fail
    //     to fetch (404, network error, beyond chart bounds) so the
    //     helm sees something useful instead of a blank rectangle.
    //   * OpenSeaMap seamark overlay (transparent) draws buoys /
    //     lights / marinas on top of whatever basemap is showing.
    // Earlier versions of this function removed one or both layers
    // when a chart was added -- that broke the fallback behaviour
    // and (for OpenSeaMap) the seamark overlay itself. The original
    // "OSM attribution leaks when OSM is inactive" concern was
    // misdiagnosed: OSM IS active (attached, fills the gaps) and the
    // attribution is correctly shown for that reason.
    const native = maxZoom || 18;
    const opts = {
        minZoom: minZoom || 1,
        maxNativeZoom: native,
        maxZoom: native,
        opacity: opacity || 0.8,
        // keepBuffer + updateWhenIdle match base layers; don't re-fetch
        // chart tiles when the user zig-zags back into territory they
        // just panned away from. updateWhenIdle follows the client-
        // strength detection -- slow devices defer tile fetches to pan-
        // end so the drag stays smooth. detectRetina on fast clients
        // sharpens chart tiles on high-DPI displays (iPad Retina would
        // otherwise blur the 256-px source up to 512 px of screen).
        // keepBuffer 10 (up from 6, default 2): ten rings of tiles
        // outside the viewport stay in DOM so route-planning pans
        // feel snappier and small zig-zags don't re-fetch.
        keepBuffer: 10,
        updateWhenIdle: isSlowClient,
        detectRetina: !isSlowClient,
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
    restackChartOpacities();
    return true;
}

export function removeChartLayer(id) {
    chartLayers.remove(id);
    // OSM + OpenSeaMap were never removed in addChartLayer (see the
    // comment there for why), so nothing to re-add here.
    restackChartOpacities();
}

// Graduated opacity for stacked charts. With one chart enabled the
// per-tile opacity is whatever the caller passed (typically 0.8);
// each chart added above gets progressively more transparent so the
// stack reads as layers rather than the topmost chart hiding what's
// underneath. Numbers are intentionally gentle -- helms reported the
// previous flat-0.8 stack hid useful detail from background charts. */
function restackChartOpacities() {
    if (!map || chartLayers.size === 0) return;
    // Order layers by their current z-index (bottom -> top). setZIndex
    // is the source of truth (setChartLayerOrder + addChartLayer's
    // initial 50 both set it), so this matches the visible draw order.
    const ordered = [];
    for (const [, layer] of chartLayers.entries()) {
        ordered.push(layer);
    }
    ordered.sort((a, b) => (a.options.zIndex ?? 50) - (b.options.zIndex ?? 50));
    if (ordered.length === 1) {
        ordered[0].setOpacity(0.85);
        return;
    }
    // Linear ramp from 0.85 (bottom) to 0.45 (top), with the bottom
    // layer always the most opaque so the helm's "primary" chart
    // dominates. 0.45 floor keeps the top layer from disappearing
    // entirely on a 4+ chart stack.
    const top = ordered.length - 1;
    for (let i = 0; i < ordered.length; i++) {
        const t = i / top;                       // 0..1, bottom->top
        const opacity = 0.85 - 0.40 * t;
        ordered[i].setOpacity(opacity);
    }
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
    restackChartOpacities();
}

// --- Routes ---

// Route polyline colour lives in MapColors.route (read from
// --ann-route at init). Call-sites use MapColors.route directly so
// a palette edit propagates without a module reload.
//
// Line-style convention used across the chartplotter:
//   - solid  = "planned" (a saved route, or future legs of an active route)
//   - dashed = "ship-to-waypoint" (bearing line, current-leg overlay)
//   - dotted = "already passed" (legs of the active route the boat crossed)
// Keep this consistent when adding new route-like overlays.

// Add a route as a polyline. coords is [[lat, lon], ...].
//
// Clicking the line opens a popup with Activate + Delete so the helm
// doesn't have to burrow into the Layers panel to do the two most
// common actions on a saved route. During route-edit / polygon-edit /
// measure the click is absorbed as a new waypoint instead (same guard
// as AIS markers) so the user can't accidentally fire Activate/Delete
// while trying to extend a route.
export function addRoute(id, name, coords) {
    if (!map || routeLayers.has(id)) return;
    const line = L.polyline(coords, {
        color: MapColors.route, weight: 2.5, opacity: 0.8
    }).addTo(map);

    // Invisible wider polyline acts as the tap hitbox. The visible
    // route is only 2.5 px wide, which is a miserable target on a
    // touch screen (users end up dropping waypoints on the map while
    // trying to activate a route). Same pattern used by the
    // route-edit flow below for live-edit vertices; do it on all
    // saved routes too so the Activate / Edit / Delete popup is
    // actually reachable from the polyline.
    const hitLine = L.polyline(coords, {
        color: MapColors.route, weight: 36, opacity: 0, interactive: true
    }).addTo(map);

    const nmTotal = routeTotalNauticalMiles(coords);
    const popupOptions = { className: 'route-popup', maxWidth: 260, autoClose: true };
    const popupHtml = () => buildRoutePopupHtml(id, name, coords.length, nmTotal);
    line.bindPopup(popupHtml(), popupOptions);
    hitLine.bindPopup(popupHtml(), popupOptions);

    const onLineClick = (ev, sourceLine) => {
        if (routeEditMode || polygonEditMode || measureActive) {
            L.DomEvent.stopPropagation(ev);
            const ll = ev.latlng;
            if (!ll) return;
            if (routeEditMode)         addEditWaypoint(ll.lat, ll.lng);
            else if (polygonEditMode)  addPolygonVertexInternal(ll.lat, ll.lng);
            else                       addMeasurePoint(ll.lat, ll.lng);
            sourceLine.closePopup();
        }
    };
    line.on('click', (ev) => onLineClick(ev, line));
    hitLine.on('click', (ev) => onLineClick(ev, hitLine));
    const wirePopup = (ev) => {
        wireRouteActivate(ev.popup, id, name);
        wireRouteEdit(ev.popup, id);
        wireDeleteConfirm(ev.popup, '.route-delete-btn', 'DeleteRouteById', id);
    };
    line.on('popupopen', wirePopup);
    hitLine.on('popupopen', wirePopup);

    // Waypoint dots at each coordinate.
    const group = L.layerGroup([line, hitLine]).addTo(map);
    for (let i = 0; i < coords.length; i++) {
        const dot = L.circleMarker(coords[i], {
            radius: 4, color: MapColors.route, fillColor: MapColors.route, fillOpacity: 1, weight: 1
        });
        dot.bindTooltip(name ? `${name} [${i + 1}]` : `WPT ${i + 1}`, { className: 'bearing-tooltip' });
        dot.addTo(group);
    }
    routeLayers.set(id, group);
}

function routeTotalNauticalMiles(coords) {
    let m = 0;
    for (let i = 1; i < coords.length; i++) {
        m += haversineMeters(coords[i - 1][0], coords[i - 1][1], coords[i][0], coords[i][1]);
    }
    return m * NM_PER_METER;
}

function buildRoutePopupHtml(id, name, wpCount, nmTotal) {
    const safeName = esc(name || `Route ${id.substring(0, 6)}`);
    return `
        <div class="route-popup-body">
            <div class="route-popup-title">${safeName}</div>
            <div class="route-popup-meta">${wpCount} WP &middot; ${nmTotal.toFixed(1)} nm</div>
            <div class="route-popup-actions">
                <button class="route-activate-btn" type="button">Activate</button>
                <button class="route-edit-btn" type="button">Edit</button>
                <button class="route-delete-btn" type="button">Delete</button>
            </div>
        </div>`;
}

// Single-tap Activate -- no two-step confirm like delete, because
// activating a route is non-destructive (the old active course is
// just replaced on the server side).
function wireRouteActivate(popup, id, name) {
    const el = popup.getElement();
    if (!el) return;
    const btn = el.querySelector('.route-activate-btn');
    if (!btn || btn._wired) return;
    btn._wired = true;
    btn.addEventListener('click', async () => {
        if (dotNetRef) {
            try { await dotNetRef.invokeMethodAsync('ActivateRouteById', id); }
            catch (_) { /* disposed or navigation in flight */ }
        }
        popup._source?.closePopup();
    });
}

// Edit button -- matches the Layers-panel Edit action. Hands off to
// C# which flips routeEditMode on and loads the polyline into the
// edit layer. Closes the popup immediately so a second tap doesn't
// land on a now-invisible button (the edit toolbar takes over the
// viewport once routeEditMode flips).
function wireRouteEdit(popup, id) {
    const el = popup.getElement();
    if (!el) return;
    const btn = el.querySelector('.route-edit-btn');
    if (!btn || btn._wired) return;
    btn._wired = true;
    btn.addEventListener('click', async () => {
        popup._source?.closePopup();
        if (dotNetRef) {
            try { await dotNetRef.invokeMethodAsync('EditRouteById', id); }
            catch (_) { /* disposed or navigation in flight */ }
        }
    });
}

// Active-route popup: same shape as the regular-route popup but the
// primary action is "Deactivate" (clear the SignalK course) rather
// than "Activate". Edit + Delete keep working on the route resource
// via the same JSInvokables the regular-route popup wires up.
function buildActiveRoutePopupHtml(id, name, wpCount, nmTotal) {
    const safeName = esc(name || `Route ${id.substring(0, 6)}`);
    return `
        <div class="route-popup-body">
            <div class="route-popup-title">${safeName}</div>
            <div class="route-popup-meta">${wpCount} WP &middot; ${nmTotal.toFixed(1)} nm &middot; active</div>
            <div class="route-popup-actions">
                <button class="route-deactivate-btn" type="button">Deactivate</button>
                <button class="route-edit-btn" type="button">Edit</button>
                <button class="route-delete-btn" type="button">Delete</button>
            </div>
        </div>`;
}

// Single-tap Deactivate -- non-destructive (the route resource stays;
// only the active SignalK course is cleared), so no two-step confirm.
// Mirrors the bottom-bar Stop Navigation button via the
// DeactivateActiveRoute [JSInvokable] on Map.razor.cs.
function wireRouteDeactivate(popup) {
    const el = popup.getElement();
    if (!el) return;
    const btn = el.querySelector('.route-deactivate-btn');
    if (!btn || btn._wired) return;
    btn._wired = true;
    btn.addEventListener('click', async () => {
        popup._source?.closePopup();
        if (dotNetRef) {
            try { await dotNetRef.invokeMethodAsync('DeactivateActiveRoute'); }
            catch (_) { /* disposed or navigation in flight */ }
        }
    });
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

// --- Active Route Navigation ---

const activeWpIcon = L.divIcon({
    className: 'active-wp-icon',
    html: '<div class="active-wp-pulse"></div>',
    iconSize: [24, 24],
    iconAnchor: [12, 12]
});

// Draw the active route as two polylines split by progress, plus
// numbered waypoint markers. Already-passed legs are dotted and
// dimmed; future legs (including the current leg from the last
// reached WP to the next) are solid. The dashed bearing line and
// active-leg overlay drawn by setCourseLine sit on top of this.
//
// wpIdx is the index of the next un-reached waypoint, resolved on
// the C# side from SignalK's next-point lat/lon. JS stays a thin
// renderer here -- no geometry, no closest-vertex lookup.
//
// routeId is the SignalK resource UUID; passing it enables tap-to-
// skip on each marker (any WP, including passed ones, can be set as
// the new next-WP via the JumpToRouteWaypoint JSInvokable on the C#
// side). Pass an empty string to disable taps -- e.g. when the active
// "course" is a single waypoint destination, not a multi-WP route.
//
// routeName is shown as the title of the tap-the-line popup
// (Deactivate / Edit / Delete) so the helm doesn't have to read a
// uuid prefix on a moving boat. Pass an empty string for unnamed
// routes -- the popup falls back to "Route <first 6 chars of id>".
export function setActiveRoute(coords, wpIdx, routeId, routeName) {
    clearActiveRoute();
    // Suppress redraw while the helm is editing the active route --
    // any in-flight SyncActiveRouteAsync that races the edit (e.g.
    // a stale data tick that fired between EditRoute clearing the
    // overlay and the suppression flag landing) would otherwise
    // re-establish the active polyline on top of the edit polyline,
    // exactly the visual mess this whole flag was added to prevent.
    //
    // Defensive log: if the flag stays "true" because a JS-side error
    // tore the C# disposal path apart (setActiveOverlayHidden(false)
    // rejected with JSDisconnectedException, swallowed silently),
    // every subsequent setActiveRoute is a quiet no-op and the helm
    // stares at a chart that won't redraw the active leg. The warning
    // makes that state visible in the console without spamming -- it
    // only fires when a setActiveRoute call was actually attempted.
    if (activeOverlayHidden) {
        console.warn('[setActiveRoute] activeOverlayHidden is still true; route render suppressed.');
        return;
    }
    if (!map || !coords || coords.length < 2) return;

    activeRouteCoords = coords;
    // Defensive bounds-check; C# already clamps but a stray NaN/-1
    // from a future caller must not crash the renderer.
    const idx = Math.max(0, Math.min(coords.length - 1, wpIdx | 0));
    activeRouteLayer = L.layerGroup().addTo(map);
    const tappable = !!(routeId && dotNetRef);

    // Already-passed legs: dotted, dim. The two polylines share the
    // boundary point coords[idx-1] so the dotted/solid handover
    // renders without a visual gap. lineCap:'round' turns the 2 px
    // dash into a true round dot, which scans more cleanly than a
    // dash at chartplotter zoom levels.
    if (idx >= 2) {
        L.polyline(coords.slice(0, idx), {
            color: MapColors.bearing,
            weight: 2,
            opacity: 0.5,
            dashArray: '2,6',
            lineCap: 'round'
        }).addTo(activeRouteLayer);
    }

    // Planned (future + current) legs: solid, full weight. Starts at
    // the last reached waypoint when idx > 0 so the current leg is
    // included.
    const futureStart = idx > 0 ? idx - 1 : 0;
    if (futureStart < coords.length - 1) {
        L.polyline(coords.slice(futureStart), {
            color: MapColors.bearing, weight: 3, opacity: 0.8
        }).addTo(activeRouteLayer);
    }

    // Tap-target hit polyline covering the whole route. Same trick as
    // addRoute: the visible polylines (2-3 px) are a miserable touch
    // target, so a 36 px transparent sibling carries the popup.
    // Bound only when we have a routeId AND a dotNetRef -- a single-
    // waypoint course (no route resource on the server) has nothing
    // for Deactivate / Edit / Delete to act on, so the popup would
    // open onto dead buttons. The Stop button in the bottom bar still
    // works in that case.
    if (tappable) {
        const hitLine = L.polyline(coords, {
            color: MapColors.bearing, weight: 36, opacity: 0, interactive: true,
        }).addTo(activeRouteLayer);
        const nmTotal = routeTotalNauticalMiles(coords);
        const popupOptions = { className: 'route-popup', maxWidth: 260, autoClose: true };
        hitLine.bindPopup(buildActiveRoutePopupHtml(routeId, routeName, coords.length, nmTotal), popupOptions);
        hitLine.on('popupopen', (ev) => {
            wireRouteDeactivate(ev.popup);
            wireRouteEdit(ev.popup, routeId);
            wireDeleteConfirm(ev.popup, '.route-delete-btn', 'DeleteRouteById', routeId);
        });
        // Same edit-mode override as the regular-route popup: while
        // the helm is in route-edit / polygon-edit / measure mode, a
        // tap should drop a vertex / measure point rather than open
        // the Deactivate popup.
        hitLine.on('click', (ev) => {
            if (routeEditMode || polygonEditMode || measureActive) {
                L.DomEvent.stopPropagation(ev);
                const ll = ev.latlng;
                if (!ll) return;
                if (routeEditMode)         addEditWaypoint(ll.lat, ll.lng);
                else if (polygonEditMode)  addPolygonVertexInternal(ll.lat, ll.lng);
                else                       addMeasurePoint(ll.lat, ll.lng);
                hitLine.closePopup();
            }
        });
    }

    // Waypoint markers (skip the next WP -- it gets the pulsing marker
    // below). Each remaining dot is tappable: clicking asks the C# side
    // to jump pointIndex to that WP, mirroring Freeboard's gesture.
    for (let i = 0; i < coords.length; i++) {
        const isPassed = i < idx;
        const isNext = i === idx;
        if (isNext) continue;

        const dot = L.circleMarker(coords[i], {
            radius: isPassed ? 3 : 5,
            color: MapColors.bearing,
            fillColor: isPassed ? '#64748b' : MapColors.bearing,
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
        if (tappable) attachJumpHandler(dot, routeId, i);
        dot.addTo(activeRouteLayer);
    }

    // Pulsing marker at the next waypoint.
    nextWpMarker = L.marker(coords[idx], {
        icon: activeWpIcon,
        zIndexOffset: 900
    }).addTo(activeRouteLayer);
    nextWpMarker.bindTooltip(`WP ${idx + 1}`, {
        permanent: true,
        direction: 'right',
        offset: [14, 0],
        className: 'route-wp-tooltip'
    });
}

// Wires a click on a route-polyline waypoint dot to the C# handler
// that PUTs /activeRoute/pointIndex with the absolute leg index.
// Stops Leaflet's bubbling so the click doesn't also pan/zoom the map
// or trigger the long-press context menu underneath.
function attachJumpHandler(layer, routeId, pointIndex) {
    layer.on('click', (e) => {
        if (e && e.originalEvent) L.DomEvent.stopPropagation(e);
        if (!dotNetRef) return;
        dotNetRef.invokeMethodAsync('JumpToRouteWaypoint', routeId, pointIndex)
            .catch(() => {});
    });
}

export function clearActiveRoute() {
    if (activeRouteLayer && map) { map.removeLayer(activeRouteLayer); }
    activeRouteLayer = null;
    activeRouteCoords = null;
    nextWpMarker = null;
}

// Toggle the "edit-active-route in progress" suppression flag. While
// hidden, applyFrame skips setCourseLine (so the leg / bearing / XTE
// tick don't keep redrawing on every position update against stale
// pre-edit geometry) and any stray setActiveRoute call during edit
// is a no-op. Also tears down the course-line elements so the helm's
// view is clean from the moment edit starts. C# pairs every true
// with a false on edit cancel / save -- the next position frame
// then redraws the course-line from the updated coords.
export function setActiveOverlayHidden(hidden) {
    activeOverlayHidden = !!hidden;
    if (hidden) {
        clearCourseLine();
    }
}

// Visually mark the active route + course line as "stopping" while we
// wait for the SK delta to confirm. Same single-source-of-truth
// pattern as setAnchorRaising: dim the elements (so the helm sees
// their tap landed) but never tear them down -- the delta drives the
// real teardown via SyncActiveRouteAsync. Iterates the layer group's
// children with setStyle so polyline + waypoint dots all dim
// together. Course-line leg + bearing + XTE tick (drawn separately
// in setCourseLine) get their own dim treatment via the same call.
export function setActiveRouteStopping(stopping) {
    if (!map) return;
    const opacity = stopping ? 0.3 : 1.0;
    const fillOpacity = stopping ? 0.3 : 1.0;
    if (activeRouteLayer) {
        activeRouteLayer.eachLayer(function (l) {
            try { l.setStyle({ opacity: opacity, fillOpacity: fillOpacity }); }
            catch (_) { /* tooltips have no setStyle; ignore */ }
        });
    }
    if (courseLineLeg) {
        try { courseLineLeg.setStyle({ opacity: opacity }); } catch (_) { }
    }
    if (typeof courseLineBearing !== 'undefined' && courseLineBearing) {
        try { courseLineBearing.setStyle({ opacity: opacity }); } catch (_) { }
    }
    if (typeof courseLineXte !== 'undefined' && courseLineXte) {
        try { courseLineXte.setStyle({ opacity: opacity }); } catch (_) { }
    }
}


// Draw/update course line: bearing line + XTE tick. Called on every
// position update when an active course exists. The previous-WP to
// next-WP "leg line" used to render here as a faint white dashed
// stroke from the spot where navigation started; helm reported it as
// noise (no actionable information beyond "where I was when the
// course started"), so it's gone. The bearing line + active-route
// polyline cover the live navigational picture.
export function setCourseLine(boatLat, boatLon, wpLat, wpLon, prevLat, prevLon, xteMeters, xteSeverity) {
    if (!map) return;

    // Tear down any leftover leg line from a previous build that
    // still emitted it. courseLineLeg stays declared at module scope
    // so dispose() can null it; this just guarantees the layer is
    // gone if some older state left it behind.
    if (courseLineLeg) {
        map.removeLayer(courseLineLeg);
        courseLineLeg = null;
    }

    // Bearing line: boat to next WP.
    const brgCoords = [[boatLat, boatLon], [wpLat, wpLon]];
    if (courseLineBearing) {
        courseLineBearing.setLatLngs(brgCoords);
    } else {
        courseLineBearing = L.polyline(brgCoords, {
            color: MapColors.bearing, weight: 2, opacity: 0.7, dashArray: '6,4'
        }).addTo(map);
    }

    // XTE perpendicular tick at boat position.
    if (xteMeters != null && prevLat != null && prevLon != null) {
        const absXte = Math.abs(xteMeters);
        // Severity is classified C#-side (Utilities/Xte.cs + XteTests) so
        // the legend, the alarm pipeline and this overlay share a single
        // set of band thresholds. Each band maps onto the shared severity
        // palette so a single edit in :root restyles both legend and tick.
        const xteColor = xteSeverity === 'offCourse' ? MapColors.mob
            : xteSeverity === 'drifting' ? MapColors.guardWarn
            : MapColors.anchorOk;
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

let routeEditHitLine = null;   // wide, transparent; used for touch-friendly tapping
// Set briefly inside insertEditVertexOnSegment; consumed by the
// map-click handler on the very next click event. Stops an
// insert-on-leg from also appending the point at the end of the
// route via the map-click fallback. A flag rather than Leaflet's
// stopPropagation because Leaflet's map click is a separate dispatch
// channel that DOM-level stopPropagation doesn't intercept.
let routeEditSuppressNextMapClick = false;

function redrawEditLine() {
    if (!routeEditLine && routeEditCoords.length >= 2) {
        routeEditLine = L.polyline(routeEditCoords, {
            color: MapColors.current, weight: 2.5, opacity: 0.8, dashArray: '8,6'
        }).addTo(routeEditLayer);
        // Wider, transparent polyline underneath as a chunky hit target.
        // On a touch screen the 2.5 px visible line is almost impossible
        // to tap without a stylus; 20 px invisible overlay fixes that
        // without thickening the rendered line. stopPropagation on both
        // click handlers prevents the map-level handler (which APPENDS
        // at the end of the route) from firing in addition to the
        // insert-between-segment handler.
        // 40 px invisible hitbox (was 20). Real sailing routes don't
        // zig-zag at 5-10 m scale, so a tap a finger-width off the
        // line almost always means "insert here" rather than "drop
        // a new point over there". Wider hitbox removes the "I tapped
        // the line and got a new endpoint instead of an insert"
        // frustration on iPad.
        routeEditHitLine = L.polyline(routeEditCoords, {
            color: MapColors.current, weight: 40, opacity: 0, interactive: true,
            // Crosshair cursor on hover so it's discoverable that
            // clicking a leg inserts a waypoint between existing ones,
            // rather than appending at the end.
            className: 'route-edit-hit-line',
        }).addTo(routeEditLayer);
        const onSegmentClick = (e) => {
            L.DomEvent.stopPropagation(e);
            insertEditVertexOnSegment(e.latlng);
        };
        routeEditHitLine.on('click', onSegmentClick);
        routeEditLine.on('click', onSegmentClick);
    } else if (routeEditLine) {
        routeEditLine.setLatLngs(routeEditCoords);
        if (routeEditHitLine) routeEditHitLine.setLatLngs(routeEditCoords);
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
    const insertAt = bestIdx + 1;
    routeEditCoords.splice(insertAt, 0, [ll.lat, ll.lng]);
    // Shift any stack entries >= insertAt up by one so historical
    // indices still point at the same waypoint objects, then push
    // the new insertion so undo finds it on top of the stack.
    for (let i = 0; i < routeEditAddStack.length; i++) {
        if (routeEditAddStack[i] >= insertAt) routeEditAddStack[i]++;
    }
    routeEditAddStack.push(insertAt);
    // Signal the map-level click handler (fires immediately after
    // this one in Leaflet's dispatch order) to skip the append-on-
    // map-click fallback. Without this the user gets a phantom Nth+1
    // waypoint at the end on every leg click.
    routeEditSuppressNextMapClick = true;
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
            color: MapColors.current, weight: 1.5, opacity: 0.7, dashArray: '3,4',
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

// Undo history. Each entry is the index of the most recently ADDED
// waypoint (append OR mid-route insert). Popping off the top and
// splicing that index back out gives the user "undo = remove the
// last thing I added", which is the intuitive semantic. The old
// undoLastEditWaypoint just popped the last coord -- when the user
// had inserted between two existing waypoints, the last coord was
// the OLD endpoint, not the just-inserted vertex, and undo felt
// wrong. See the ticket: "route edit undo does strange things".
let routeEditAddStack = [];

function addEditWaypoint(lat, lon) {
    const idx = routeEditCoords.length;
    routeEditCoords.push([lat, lon]);
    routeEditAddStack.push(idx);

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
    routeEditHitLine = null;
    routeEditAddStack = [];
}

export function getEditRouteCoords() {
    return routeEditCoords;
}

export function undoLastEditWaypoint() {
    if (routeEditCoords.length === 0) return;
    // Pop the index of the most recently added waypoint. For pure
    // appends this is always "remove the last"; for mid-route
    // inserts it's the inserted vertex, which is what the user
    // actually wanted undone. Fallback to last-coord pop when the
    // stack is empty (happens after a loadRouteForEdit hydrate --
    // the existing coords weren't "added" in this session).
    let removeIdx;
    if (routeEditAddStack.length > 0) {
        removeIdx = routeEditAddStack.pop();
    } else {
        removeIdx = routeEditCoords.length - 1;
    }
    if (removeIdx < 0 || removeIdx >= routeEditCoords.length) {
        removeIdx = routeEditCoords.length - 1;
    }
    // Shift any remaining stack entries above the removal point down
    // so they keep pointing at the same waypoint objects after the
    // splice renumbers everything below them.
    for (let i = 0; i < routeEditAddStack.length; i++) {
        if (routeEditAddStack[i] > removeIdx) routeEditAddStack[i]--;
    }
    routeEditCoords.splice(removeIdx, 1);
    rebuildRouteEditMarkers();
    redrawEditLine();
}

// Reverse the order of all edit waypoints in place. Used when the
// user wants to flip a route's direction (e.g. they planned outbound
// and now need the return leg). The marker numbers and the polyline
// are rebuilt from the reversed coord array; the add-stack is wiped
// because per-vertex add-order tracking is meaningless after a flip.
export function reverseEditRoute() {
    if (routeEditCoords.length < 2) return;
    routeEditCoords.reverse();
    routeEditAddStack = [];
    rebuildRouteEditMarkers();
    redrawEditLine();
}

// Remove a specific waypoint by index (called from the in-panel list).
// Removing the middle of an N-point route means every subsequent marker's
// number changes, so we tear down the dragging markers and rebuild from
// the coord array. The polyline is re-used (setLatLngs) for cheapness.
export function removeRouteEditWaypoint(index) {
    if (index < 0 || index >= routeEditCoords.length) return;
    routeEditCoords.splice(index, 1);
    // Shift any add-stack entries. Anything at >index drops one;
    // anything == index is dropped (the user explicitly removed
    // it via the in-panel list, not via undo).
    routeEditAddStack = routeEditAddStack
        .filter(i => i !== index)
        .map(i => i > index ? i - 1 : i);
    if (routeEditCoords.length < 2 && routeEditLine && routeEditLayer) {
        routeEditLayer.removeLayer(routeEditLine);
        if (routeEditHitLine) routeEditLayer.removeLayer(routeEditHitLine);
        routeEditLine = null;
        routeEditHitLine = null;
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
    // The loaded waypoints weren't "added" in this edit session --
    // the user didn't tap them here, they came from the server. Clear
    // the stack so Undo only removes vertices the user added AFTER
    // opening the existing route for edit.
    routeEditAddStack = [];
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

// Polygon edit uses the same violet as route-edit so "I am editing"
// reads consistently across both drawing modes. Was amber
// (--ann-region, #d4a850) which matched finished regions but fought
// the route-edit cue.  User feedback: keep the in-edit colour the
// same regardless of shape; saved-region amber kicks in on save.
const POLYGON_COLOR = '#a78bfa';                    // --map-current

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

// Seed an existing polygon's vertices into edit mode. Called from the
// Layers-panel Edit button on a region row. Mirrors loadRouteForEdit.
export function loadPolygonForEdit(coords) {
    stopPolygonEdit();
    polygonEditMode = true;
    polygonEditLayer = L.layerGroup().addTo(map);
    if (!coords) return;
    for (const c of coords) {
        addPolygonVertexInternal(c[0], c[1]);
    }
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

// Formats the hover-tooltip content for a waypoint marker: name (or
// short id if unnamed) above a compact coordinate pair. Returned as
// HTML so the tooltip can break onto two lines -- plain-string
// tooltips can't wrap. Kept as a free function so map-restart
// rebuilds use the same format as the initial addWaypointMarker.
function formatWaypointTooltip(name, id, lat, lon) {
    const title = name || (id ? id.substring(0, 8) : 'Waypoint');
    const ns = lat >= 0 ? 'N' : 'S';
    const ew = lon >= 0 ? 'E' : 'W';
    const coords = `${Math.abs(lat).toFixed(5)}\u00B0 ${ns}, ${Math.abs(lon).toFixed(5)}\u00B0 ${ew}`;
    return `<div class="wp-tooltip-name">${esc(title)}</div>` +
           `<div class="wp-tooltip-coords">${esc(coords)}</div>`;
}

export function addWaypointMarker(id, lat, lon, name) {
    if (!map || waypointMarkers.has(id)) return;
    const marker = L.circleMarker([lat, lon], {
        radius: 6, color: WAYPOINT_COLOR, fillColor: WAYPOINT_COLOR, fillOpacity: 1, weight: 2,
        interactive: false,
    });
    // Wider invisible hit-buffer so a finger-wide tap registers.
    // 6 px visible radius = 12 px target; bumped to 22 px here gives
    // a 44 px hit (iPad WCAG floor). Visual marker stays 6 px so the
    // chart doesn't look cluttered.
    const hit = L.circleMarker([lat, lon], {
        radius: 22, opacity: 0, fillOpacity: 0, weight: 0, interactive: true
    });
    // Tooltip on hover (quick identification); popup on click (full
    // name + Delete). Same pattern as notes/regions so the tap-to-act
    // affordance is consistent across user-placed objects.
    //
    // Coordinates now accompany the name: F5 precision gives ~1 m
    // resolution which is what a helm reading coords off a chart
    // actually needs, without pretending to a decimal of longitude
    // that GPS jitter already eats. Hemisphere letters (N/S, E/W)
    // keep the reading unambiguous when the waypoint is near the
    // equator or the prime meridian.
    // Events fire on the hit buffer; the visible marker is non-
    // interactive so the two don't double-handle.
    hit.bindTooltip(formatWaypointTooltip(name, id, lat, lon), {
        permanent: false, direction: 'right', offset: [10, 0],
        className: 'bearing-tooltip'
    });
    hit.bindPopup(buildWaypointPopupHtml(id, name, lat, lon), {
        className: 'note-popup',
        maxWidth: 280,
        autoClose: true,
        closeButton: false,
    });
    hit.on('click', (ev) => {
        // During edit modes, swallow the click and forward the waypoint's
        // location to whatever the user is plotting -- matches the note
        // marker's edit-mode behaviour.
        if (routeEditMode || polygonEditMode || measureActive) {
            L.DomEvent.stopPropagation(ev);
            const ll = ev.latlng || marker.getLatLng();
            if (routeEditMode)         addEditWaypoint(ll.lat, ll.lng);
            else if (polygonEditMode)  addPolygonVertexInternal(ll.lat, ll.lng);
            else                       addMeasurePoint(ll.lat, ll.lng);
            hit.closePopup();
        }
    });
    hit.on('popupopen', (ev) => wireDeleteConfirm(ev.popup, '.waypoint-delete-btn', 'DeleteWaypoint', id));
    // Group + add-to-map so remove/clear takes both layers down
    // together. MarkerLayer.remove -> map.removeLayer(group) which
    // removes its children.
    const group = L.layerGroup([marker, hit]).addTo(map);
    waypointMarkers.set(id, group);
}

export function removeWaypointMarker(id) {
    if (!map) return;
    waypointMarkers.remove(id);
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
    // Modern sticky-note pin, 22x28. Rounded-corner card (no skeuomorphic
    // folded-corner), white text strokes for better contrast against the
    // amber fill, clean teardrop tail pointing down to the map coord.
    // Anchor is bottom-centre so the tip of the tail lands on the target
    // lat/lon. Softer drop-shadow than the v1 icon so the pin lifts off
    // the chart without adding visual noise.
    const svg = `
        <svg width="22" height="28" viewBox="0 0 22 28" xmlns="http://www.w3.org/2000/svg"
             style="filter: drop-shadow(0 1.5px 2px rgba(0,0,0,0.35));">
            <rect x="2" y="2" width="18" height="18" rx="4" ry="4"
                  fill="${NOTE_COLOR}" stroke="${NOTE_COLOR_STROKE}" stroke-width="1.2"/>
            <line x1="6"  y1="8"  x2="16" y2="8"
                  stroke="rgba(255,255,255,0.92)" stroke-width="1.4" stroke-linecap="round"/>
            <line x1="6"  y1="12" x2="16" y2="12"
                  stroke="rgba(255,255,255,0.92)" stroke-width="1.4" stroke-linecap="round"/>
            <line x1="6"  y1="16" x2="12" y2="16"
                  stroke="rgba(255,255,255,0.92)" stroke-width="1.4" stroke-linecap="round"/>
            <path d="M8 20 Q11 20 11 26 Q11 20 14 20 Z"
                  fill="${NOTE_COLOR}" stroke="${NOTE_COLOR_STROKE}" stroke-width="1.2"
                  stroke-linejoin="round"/>
        </svg>`;
    return L.divIcon({
        className: 'note-icon',
        html: svg,
        iconSize: [22, 28],
        iconAnchor: [11, 28],
        popupAnchor: [0, -26],
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
    // During edit modes, swallow the click and append to whatever the
    // user is building. Same guard as AIS markers.
    marker.on('click', (ev) => {
        if (routeEditMode || polygonEditMode || measureActive) {
            L.DomEvent.stopPropagation(ev);
            const ll = ev.latlng || marker.getLatLng();
            if (routeEditMode)         addEditWaypoint(ll.lat, ll.lng);
            else if (polygonEditMode)  addPolygonVertexInternal(ll.lat, ll.lng);
            else                       addMeasurePoint(ll.lat, ll.lng);
            marker.closePopup();
        }
    });
    // Wire up the delete button when the popup opens. We query within the
    // popup DOM so an id collision with something else on the page can't
    // hijack the click.
    marker.on('popupopen', (ev) => wireDeleteConfirm(ev.popup, '.note-delete-btn', 'DeleteNote', id));
    noteMarkers.set(id, marker);
}

// Two-step confirm wiring for a popup's delete button. First click
// swaps the label to "Really?" (intentionally short so the button's
// pixel width stays close to the original "Delete" label and the
// surrounding popup layout doesn't reflow under the helm's finger);
// second click within 3 seconds triggers the actual server delete
// via the C# [JSInvokable] method. A passing tap in rough weather
// is the nightmare case; the confirm-and-timeout pattern matches
// how native iOS/Android apps guard destructive actions without
// pulling up a full confirm dialog.
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
            btn.textContent = 'Really?';
            confirmTimer = setTimeout(reset, 3000);
            return;
        }
        reset();
        if (dotNetRef) dotNetRef.invokeMethodAsync(dotNetMethod, id).catch(() => {});
    });
    // Closing the popup resets confirm state so re-opening starts fresh.
    popup.once('popupclose', reset);
}

function buildWaypointPopupHtml(id, name, lat, lon) {
    const safeName = esc(name || id.substring(0, 8));
    // Coords mirror the hover-tooltip format (5dp ~ 1 m, hemisphere
    // letters) so hover-then-tap doesn't show two conflicting
    // renderings of the same position. Tap-only users (phones, iPad)
    // need the coords here because they never trigger hover.
    const ns = lat >= 0 ? 'N' : 'S';
    const ew = lon >= 0 ? 'E' : 'W';
    const coords = `${Math.abs(lat).toFixed(5)}\u00B0 ${ns}, ${Math.abs(lon).toFixed(5)}\u00B0 ${ew}`;
    return `
        <div class="note-popup-inner">
            <div class="note-popup-title">${safeName}</div>
            <div class="note-popup-coords">${esc(coords)}</div>
            <button class="waypoint-delete-btn note-delete-btn" type="button">Delete</button>
        </div>`;
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
// Fit a lat/lon rectangle into the viewport. Used by deep-links from
// the Resources page for routes / regions so "View" does a sensible
// zoom-to-extents rather than dropping at an arbitrary zoom. Padding
// is 40 px per side so the subject isn't flush against a HUD edge.
export function fitBounds(minLat, minLon, maxLat, maxLon) {
    if (!map) return;
    const bounds = L.latLngBounds([[minLat, minLon], [maxLat, maxLon]]);
    map.fitBounds(bounds, { padding: [40, 40], maxZoom: 16 });
}

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
        // Route / polygon / measure edit: clicks on regions append to
        // the in-progress shape instead of opening the region popup.
        poly.on('click', (ev) => {
            if (routeEditMode || polygonEditMode || measureActive) {
                L.DomEvent.stopPropagation(ev);
                const ll = ev.latlng;
                if (!ll) return;
                if (routeEditMode)         addEditWaypoint(ll.lat, ll.lng);
                else if (polygonEditMode)  addPolygonVertexInternal(ll.lat, ll.lng);
                else                       addMeasurePoint(ll.lat, ll.lng);
                poly.closePopup();
            }
        });
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
    // Country flag from signalk-flags plugin when we know the own MMSI.
    // Same onerror hide as AIS markers (plugin not installed = silent).
    const flagHtml = ownMmsi
        ? `<img class="ais-popup-flag" src="${flagUrl(ownMmsi)}" alt="" onerror="this.style.display='none'">`
        : '';
    return `<div class="ais-popup-content">` +
        `<div class="ais-popup-title">${flagHtml}&#9733; Own boat</div>` +
        `<table class="ais-popup-table">` +
            `<tr><td>Pos</td><td>${pos}</td></tr>` +
            `<tr><td>SOG</td><td>${sog} kn</td></tr>` +
            `<tr><td>COG</td><td>${cogDeg}&deg;</td></tr>` +
            `<tr><td>HDG</td><td>${hdgDeg}&deg;</td></tr>` +
        `</table>` +
        `</div>`;
}

/** Set the own-boat MMSI. C# calls this once SignalkClient has
 *  resolved self-context from the hello message. Idempotent; calling
 *  with the same value is a no-op. */
export function setOwnMmsi(mmsi) {
    ownMmsi = mmsi || null;
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

export function setCurrentArrow(boatLat, boatLon, setRad, driftMs) {
    if (!map) return;
    // Arrow length proportional to drift, min 200m, max 2000m visual.
    // Magnitude is already encoded in the arrow length; the tooltip that
    // used to print "1.5kn" next to the arrow head was dropped on user
    // request -- it read like a loose label on the chart and the drift
    // value is redundant with what the bottom-right HUD already shows.
    const arrowLen = Math.min(Math.max(driftMs * 600, 200), 2000);
    const endPt = destPoint(boatLat, boatLon, setRad, arrowLen);

    if (currentArrow) {
        currentArrow.setLatLngs([[boatLat, boatLon], endPt]);
    } else {
        currentArrow = L.polyline([[boatLat, boatLon], endPt], {
            color: MapColors.current, weight: 3, opacity: 0.8
        }).addTo(map);
    }
}

export function clearCurrentArrow() {
    if (currentArrow && map) { map.removeLayer(currentArrow); currentArrow = null; }
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

    // Laylines follow the anchor-ok / mob palette for stbd/port --
    // green = "safe tack", red = "other tack". Legend doesn't show
    // laylines as a swatch currently but the palette stays consistent
    // with the anchor and MOB cues (same severity metaphor).
    if (laylineStarboard) laylineStarboard.setLatLngs([[boatLat, boatLon], stbdEnd]);
    else {
        laylineStarboard = L.polyline([[boatLat, boatLon], stbdEnd], {
            color: MapColors.anchorOk, weight: 2, opacity: 0.7, dashArray: '10,6'
        }).addTo(map);
    }

    if (laylinePort) laylinePort.setLatLngs([[boatLat, boatLon], portEnd]);
    else {
        laylinePort = L.polyline([[boatLat, boatLon], portEnd], {
            color: MapColors.mob, weight: 2, opacity: 0.7, dashArray: '10,6'
        }).addTo(map);
    }

    // Waypoint laylines (from waypoint back toward the wind).
    if (wpLat != null && wpLon != null) {
        const wpStbdEnd = destPoint(wpLat, wpLon, stbdBrg + Math.PI, lineLen);
        const wpPortEnd = destPoint(wpLat, wpLon, portBrg + Math.PI, lineLen);

        if (laylineWpStarboard) laylineWpStarboard.setLatLngs([[wpLat, wpLon], wpStbdEnd]);
        else {
            laylineWpStarboard = L.polyline([[wpLat, wpLon], wpStbdEnd], {
                color: MapColors.anchorOk, weight: 1.5, opacity: 0.35, dashArray: '6,6'
            }).addTo(map);
        }
        if (laylineWpPort) laylineWpPort.setLatLngs([[wpLat, wpLon], wpPortEnd]);
        else {
            laylineWpPort = L.polyline([[wpLat, wpLon], wpPortEnd], {
                color: MapColors.mob, weight: 1.5, opacity: 0.35, dashArray: '6,6'
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

        // Don't hijack browser shortcuts. Ctrl+R / Cmd+R (reload),
        // Ctrl+W (close tab), etc. all involve a modifier -- the map's
        // single-letter shortcuts don't, so dropping modifier combos
        // here is harmless and stops us clobbering "r" -> reload on
        // desktop Chromium + Safari. Arrow keys still fire below even
        // with Shift (Shift = coarse pan) but no other combos.
        if (e.ctrlKey || e.metaKey || e.altKey) return;

        // Arrow keys pan the map. Leaflet's built-in keyboard handler
        // requires the map container to have focus, which gets lost
        // whenever the user clicks any other element -- in practice
        // arrows just scrolled the page. Drive it directly so arrows
        // always pan the chart, regardless of focus.
        //
        // Step size: 1/5 of viewport by default. Earlier 1/3 lurched
        // the chart noticeably with each tap; user feedback was that
        // a smaller step gives better fine-positioning at the helm
        // without going as small as the previous 1/8. Shift = full
        // viewport for coarse scrubbing across passages.
        if (map && (e.key === 'ArrowUp' || e.key === 'ArrowDown'
                    || e.key === 'ArrowLeft' || e.key === 'ArrowRight'))
        {
            e.preventDefault();
            const size = map.getSize();
            const frac = e.shiftKey ? 1.0 : 1 / 5;
            let dx = 0, dy = 0;
            switch (e.key) {
                case 'ArrowUp':    dy = -size.y * frac; break;
                case 'ArrowDown':  dy =  size.y * frac; break;
                case 'ArrowLeft':  dx = -size.x * frac; break;
                case 'ArrowRight': dx =  size.x * frac; break;
            }
            // A manual pan should drop follow-mode; otherwise the next
            // position delta would yank the view back. Mirror the
            // mouse-drag break-follow contract (suppressMoveEnd isn't
            // what we want here -- that suppresses the callback, not
            // the follow flag).
            if (followBoat) {
                followBoat = false;
                if (dotNetRef) dotNetRef.invokeMethodAsync('SetFollowFromJs', false).catch(() => {});
            }
            map.panBy([dx, dy], { animate: true, duration: 0.3 });
            return;
        }

        const key = e.key.toLowerCase();
        const isLetter = 'mfnatlor'.includes(key) && key.length === 1;
        const isSpecial = key === '?' || key === 'escape';
        if (isLetter || isSpecial) {
            e.preventDefault();
            dotNetObjRef.invokeMethodAsync('OnKeyShortcut', key).catch(() => {});
        }
    };
    document.addEventListener('keydown', keyHandler);
    // Mark on the document so tests can wait for the handler to be
    // ready. Also useful for debugging "did the shortcut listener
    // attach?" without a network probe.
    document.documentElement.setAttribute('data-ona-key-shortcuts', 'on');
}

export function disableKeyboardShortcuts() {
    if (keyHandler) {
        document.removeEventListener('keydown', keyHandler);
        keyHandler = null;
        document.documentElement.removeAttribute('data-ona-key-shortcuts');
    }
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

// --- AtoN (Aids to Navigation) ---
//
// AIS Type 21 marks: cardinal/lateral/special buoys, beacons,
// lighthouses, racons. Render as L.divIcon so we don't have to ship
// 16 SVG files; the icon-builder draws inline SVG sized to the marker
// and tinted by symbol kind. Virtual AtoNs (no physical mark in the
// water -- e.g. wreck warnings) get a dashed outline so the helm
// doesn't go looking for an actual buoy.
//
// AtoNs are static enough that the C# side pushes the full set on
// store-change rather than per-update. setAtons replaces the entire
// marker layer; ids that go away in the new payload are removed.

const atonMarkers = new MarkerLayer();

// IALA Region A palette. Cardinal marks use yellow + black bands;
// lateral red = port (Region A), green = starboard. Other marks
// (isolated danger, safe water, special) carry their own palette.
const ATON_COLOR_PORT = '#d62828';      // red lateral (Region A)
const ATON_COLOR_STBD = '#06a13a';      // green lateral (Region A)
const ATON_COLOR_CARDINAL_Y = '#f4c430'; // amber-yellow
const ATON_COLOR_CARDINAL_K = '#1b1b1b'; // near-black
const ATON_COLOR_DANGER = '#1b1b1b';    // isolated danger (black with red bands)
const ATON_COLOR_DANGER_BAND = '#d62828';
const ATON_COLOR_SAFE = '#d62828';      // safe water (red+white vertical stripes)
const ATON_COLOR_SPECIAL = '#f4c430';   // yellow with X topmark
const ATON_COLOR_BASE = '#3b82f6';      // base station (blue square)
const ATON_COLOR_UNKNOWN = '#6b7280';

// Build a 28x28 SVG markup string for the given (symbol, side) pair.
// Coordinates assume (14, 14) is the centre; the L.divIcon iconAnchor
// places the centre on the lat/lon. Virtual marks ride a dashed
// stroke; real marks get a solid stroke.
function buildAtonSvg(symbol, side, isVirtual) {
    const stroke = isVirtual ? '4 2' : '0';
    const strokeWidth = 1.5;
    if (symbol === 'Cardinal') {
        // Two stacked black/yellow cones; orientation by cardinal side
        // (north = double-up, south = double-down, east = up+down,
        // west = down+up). Width 14, height 24 centred at (14,14).
        const yTop = side === 'North' || side === 'East'
            ? ATON_COLOR_CARDINAL_K : ATON_COLOR_CARDINAL_Y;
        const yBot = side === 'South' || side === 'East'
            ? ATON_COLOR_CARDINAL_K : ATON_COLOR_CARDINAL_Y;
        return `<svg xmlns="http://www.w3.org/2000/svg" width="28" height="28" viewBox="0 0 28 28">
            <rect x="9" y="3" width="10" height="10" fill="${yTop}" stroke="#000" stroke-width="${strokeWidth}" stroke-dasharray="${stroke}" />
            <rect x="9" y="13" width="10" height="10" fill="${yBot}" stroke="#000" stroke-width="${strokeWidth}" stroke-dasharray="${stroke}" />
            <text x="14" y="18" text-anchor="middle" font-size="11" font-weight="bold" fill="#fff" font-family="sans-serif">${side[0]}</text>
        </svg>`;
    }
    if (symbol === 'Lateral') {
        // Region A: port=red can, starboard=green cone.
        const fill = side === 'Port' ? ATON_COLOR_PORT : ATON_COLOR_STBD;
        const shape = side === 'Port'
            // Can shape (rectangle with flat top)
            ? `<rect x="6" y="6" width="16" height="16" fill="${fill}" stroke="#000" stroke-width="${strokeWidth}" stroke-dasharray="${stroke}" />`
            // Cone shape (triangle pointing up)
            : `<polygon points="14,4 22,22 6,22" fill="${fill}" stroke="#000" stroke-width="${strokeWidth}" stroke-dasharray="${stroke}" />`;
        return `<svg xmlns="http://www.w3.org/2000/svg" width="28" height="28" viewBox="0 0 28 28">${shape}</svg>`;
    }
    if (symbol === 'IsolatedDanger') {
        // Black sphere with a red horizontal band, two black topmark
        // balls. Simplified to a circle for legibility at marker size.
        return `<svg xmlns="http://www.w3.org/2000/svg" width="28" height="28" viewBox="0 0 28 28">
            <circle cx="14" cy="14" r="9" fill="${ATON_COLOR_DANGER}" stroke="#000" stroke-width="${strokeWidth}" stroke-dasharray="${stroke}" />
            <rect x="5" y="11" width="18" height="6" fill="${ATON_COLOR_DANGER_BAND}" />
            <text x="14" y="18" text-anchor="middle" font-size="11" font-weight="bold" fill="#fff" font-family="sans-serif">!</text>
        </svg>`;
    }
    if (symbol === 'SafeWater') {
        // Red and white vertical stripes, single sphere topmark.
        return `<svg xmlns="http://www.w3.org/2000/svg" width="28" height="28" viewBox="0 0 28 28">
            <circle cx="14" cy="14" r="9" fill="#fff" stroke="#000" stroke-width="${strokeWidth}" stroke-dasharray="${stroke}" />
            <path d="M14 5 L14 23" stroke="${ATON_COLOR_SAFE}" stroke-width="6" />
        </svg>`;
    }
    if (symbol === 'Special') {
        // Yellow X-mark.
        return `<svg xmlns="http://www.w3.org/2000/svg" width="28" height="28" viewBox="0 0 28 28">
            <circle cx="14" cy="14" r="9" fill="${ATON_COLOR_SPECIAL}" stroke="#000" stroke-width="${strokeWidth}" stroke-dasharray="${stroke}" />
            <path d="M9 9 L19 19 M19 9 L9 19" stroke="#000" stroke-width="2" />
        </svg>`;
    }
    if (symbol === 'BaseStation') {
        // Antenna icon: square plus radiating lines. Reuses the AtoN
        // marker layer because shore.basestations.* arrives on the
        // same delta path.
        return `<svg xmlns="http://www.w3.org/2000/svg" width="28" height="28" viewBox="0 0 28 28">
            <rect x="6" y="14" width="16" height="10" fill="${ATON_COLOR_BASE}" stroke="#000" stroke-width="${strokeWidth}" />
            <path d="M14 14 L14 4 M10 7 L18 7 M11 4 L17 4" stroke="${ATON_COLOR_BASE}" stroke-width="2" fill="none" />
        </svg>`;
    }
    // Unknown / unmapped: small grey diamond so the helm sees that
    // SOMETHING is there even when the type code didn't match.
    return `<svg xmlns="http://www.w3.org/2000/svg" width="28" height="28" viewBox="0 0 28 28">
        <polygon points="14,5 23,14 14,23 5,14" fill="${ATON_COLOR_UNKNOWN}" stroke="#000" stroke-width="${strokeWidth}" stroke-dasharray="${stroke}" />
    </svg>`;
}

function makeAtonIcon(symbol, side, isVirtual) {
    return L.divIcon({
        className: 'aton-marker',  // CSS hook for global styling
        html: buildAtonSvg(symbol, side, isVirtual),
        iconSize: [28, 28],
        iconAnchor: [14, 14],
    });
}

function buildAtonPopupHtml(a) {
    const titleParts = [];
    if (a.name) titleParts.push(a.name);
    else if (a.mmsi) titleParts.push(a.mmsi);
    else titleParts.push('AtoN');
    if (a.virtual) titleParts.push('(virtual)');
    const title = titleParts.join(' ');
    const subtitle = a.typeName || (a.typeId != null ? `Type ${a.typeId}` : '');
    return `<div class="aton-popup">
        <strong>${title}</strong>
        ${subtitle ? `<div class="aton-popup-sub">${subtitle}</div>` : ''}
        ${a.mmsi && a.name ? `<div class="aton-popup-mmsi">MMSI ${a.mmsi}</div>` : ''}
    </div>`;
}

/**
 * Replace the rendered AtoN set. Adds new ids, updates moved entries
 * (rare -- AtoNs don't usually move), removes ids missing from the
 * payload. Caller (Map.razor) pushes the whole snapshot from
 * AtonStore on each OnAtonsUpdated event; the layer is small enough
 * (typical harbour 10-50 entries, big port maybe 200) that a full
 * rebuild on every change isn't a perf problem.
 *
 * @param {Array<{
 *   context: string, name?: string, mmsi?: string,
 *   lat: number, lon: number,
 *   typeId?: number, typeName?: string,
 *   symbol: string, side: string, virtual?: boolean
 * }>} atons
 */
export function setAtons(atons) {
    if (!map) return;
    const seen = new Set();
    for (const a of atons) {
        if (a.lat == null || a.lon == null
            || !isFinite(a.lat) || !isFinite(a.lon)) continue;
        seen.add(a.context);
        const icon = makeAtonIcon(a.symbol, a.side, !!a.virtual);
        const existing = atonMarkers.get(a.context);
        if (existing) {
            existing.setLatLng([a.lat, a.lon]);
            existing.setIcon(icon);
            existing.setPopupContent(buildAtonPopupHtml(a));
        } else {
            // Honour the visibility flag on creation. Without this
            // guard a setAtons that runs while atonsVisible=false
            // would silently add fresh markers to the map -- the user
            // hides the layer, a reconnect repopulates the store, and
            // the buoys reappear despite the toggle being off.
            const m = L.marker([a.lat, a.lon], { icon })
                .bindPopup(buildAtonPopupHtml(a), { autoPan: false });
            if (atonsVisible) m.addTo(map);
            atonMarkers.set(a.context, m);
        }
    }
    // Remove ids no longer in the snapshot.
    for (const id of atonMarkers.keys()) {
        if (!seen.has(id)) atonMarkers.remove(id);
    }
}

/** Visibility toggle. Hides without losing the marker layer state so
 *  a re-show doesn't have to re-fetch. The layer remains registered
 *  with Leaflet -- we just remove from / add to the map. */
let atonsVisible = true;
export function setAtonsVisible(visible) {
    if (!map) return;
    if (atonsVisible === !!visible) return;
    atonsVisible = !!visible;
    for (const id of atonMarkers.keys()) {
        const m = atonMarkers.get(id);
        if (!m) continue;
        if (atonsVisible) m.addTo(map);
        else map.removeLayer(m);
    }
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
    // Clear any pending move-end debounce before tearing down so a
    // straggling setTimeout can't resume into a disposed map.
    if (boundsTimer) { clearTimeout(boundsTimer); boundsTimer = null; }
    // Null dotNetRef BEFORE tearing down the map. Leaflet's map.remove()
    // fires 'unload' synchronously; any handler that tries to call
    // dotNetRef.invokeMethodAsync during unload would otherwise hit a
    // still-live reference that C# has already disposed, producing the
    // "no tracked object with id X" error in the console.
    dotNetRef = null;
    if (map) { map.remove(); map = null; }
    boatMarker = null; boatVector = null; vectorLabel = null; trackLayer = null;
    osmBaseLayer = null; seaBaseLayer = null; serverTrackLayer = null;
    chartLayers.clear();
    routeLayers.clear();
    for (const id of Object.keys(aisLabels)) delete aisLabels[id];
    mobMarker = null; mobCircle = null; mobLine = null; mobLabel = null;
    anchorMarker = null; anchorCircle = null; anchorTrailLayer = null;
    anchorRadiusLine = null;
    anchorTrail.length = 0;
    activeRouteLayer = null; activeRouteCoords = null; nextWpMarker = null;
    courseLineLeg = null; courseLineBearing = null; courseLineXte = null;
    laylineStarboard = null; laylinePort = null;
    laylineWpStarboard = null; laylineWpPort = null;
    // `currentLabel` used to exist as a sibling of `currentArrow` for
    // a drift-speed tooltip on the tidal-current arrow; that label was
    // dropped but the assignment lingered here under ES module strict
    // mode, throwing ReferenceError on every dispose() and surfacing
    // as "Unhandled exception rendering component" in Blazor's error
    // boundary when the user navigated off the Chart page. Removed.
    currentArrow = null;
    weatherLayer = null;
    routeEditMode = false; routeEditLayer = null;
    routeEditCoords = []; routeEditMarkers = []; routeEditLine = null;
    polygonEditMode = false; polygonEditLayer = null;
    polygonEditCoords = []; polygonEditMarkers = [];
    polygonEditShape = null; polygonEditLine = null;
    waypointMarkers.clear();
    noteMarkers.clear();
    regionLayers.clear();
    atonMarkers.clear();
    for (const ctx of Object.keys(aisMarkers)) delete aisMarkers[ctx];
    for (const ctx of Object.keys(aisVectors)) delete aisVectors[ctx];
    for (const ctx of Object.keys(aisCpaOwnLines)) delete aisCpaOwnLines[ctx];
    for (const ctx of Object.keys(aisCpaTgtLines)) delete aisCpaTgtLines[ctx];
    for (const ctx of Object.keys(aisCpaLabels)) delete aisCpaLabels[ctx];
    for (const ctx of Object.keys(aisTrailLines)) delete aisTrailLines[ctx];
    for (const ctx of Object.keys(aisTrailHistory)) delete aisTrailHistory[ctx];
    guardZoneRing = null;
    // dotNetRef is now nulled at the TOP of dispose() so map.remove()'s
    // synchronous unload handlers can't race into a half-disposed ref.
}
