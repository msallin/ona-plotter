// Leaflet JS interop for the chartplotter map.
// All map state lives here; Blazor calls exported functions via IJSRuntime.

import { DEG, NM_PER_METER, VECTOR_MINUTES, SPEED_BUCKETS,
         haversineMeters, destPoint, vectorEnd,
         speedColor, speedBucket } from './geoMath.js';
import { MarkerLayer } from './markerLayer.js';
import { enableRadarOverlay, disableRadarOverlay,
         setRadarRange, setBoatState as setRadarBoatState } from './radarLayer.js';
import * as weatherLayerMod from './weatherLayer.js';
import * as anchorLayerMod from './anchorLayer.js';
import * as mobLayerMod from './mobLayer.js';
import * as laylineLayerMod from './laylineLayer.js';
import * as atonLayerMod from './atonLayer.js';
import * as measureLayerMod from './measureLayer.js';
import * as aisLayerMod from './aisLayer.js';
import { withOverzoom } from './overzoomLayer.js';
import { decideDownshift } from './chartDownshift.js';
import * as activeRouteLayerMod from './activeRouteLayer.js';
import * as courseLineLayerMod from './courseLineLayer.js';
import * as routeEditLayerMod from './routeEditLayer.js';
import * as polygonEditLayerMod from './polygonEditLayer.js';
import * as waypointLayerMod from './waypointLayer.js';
import * as noteLayerMod from './noteLayer.js';
import * as regionLayerMod from './regionLayer.js';
import { esc as escImported, wireDeleteConfirm as wireDeleteConfirmImported } from './popupHelpers.js';

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
let boatVectorTip = null;  // Filled dot at the COG-vector end -- matches AIS layer's tip.
let vectorLabel = null;  // Time/distance label at end of COG vector.
let trackLayer = null;
let followBoat = true;
let mapOrientation = 'north'; // 'north', 'course', 'head'
let currentRotationDeg = 0;
let nightMode = false;
let dotNetRef = null;
let suppressMoveEnd = false;  // Suppress moveend during programmatic panTo.

// AIS state lives in aisLayer.js.

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

/** Stash the History page's DotNetObjectReference on window so the
 *  Leaflet moveend handler attached inline (via eval) can invoke
 *  back into C#. Replaces an earlier `eval("window._historyDotNet
 *  = arguments[0]", ref)` call which threw "arguments is not
 *  defined" -- eval runs in the surrounding script/module strict
 *  context where `arguments` isn't bound, and Blazor's eval interop
 *  doesn't pass extra args to the eval'd code anyway. A real
 *  exported function via JSObjectReference.InvokeVoidAsync DOES
 *  receive serialised args (DotNetObjectReference round-trips as
 *  a JS proxy), so this is the correct shape for the handoff. */
export function setHistoryDotNetRef(ref) {
    window._historyDotNet = ref;
}

/** Build the flag-image URL for an MMSI. Same path on every server
 *  (signalk-flags plugin); only the origin varies. Used by the
 *  own-boat popup and the AIS layer module. */
function flagUrl(mmsi) {
    return `${signalKBaseUrl}/signalk/v2/api/resources/flags/mmsi/${encodeURIComponent(mmsi)}`;
}

// Guard-zone state is owned by aisLayer.js along with the rest of the
// CPA pipeline -- the ring is the visual companion to the alarm
// thresholds it carries.

// MarkerLayer now lives in markerLayer.js so it's unit-testable in
// node (see markerLayer.test.js). Each instance needs a reference to
// the Leaflet map for removeLayer() -- since `map` gets assigned in
// initMap *after* these dicts are constructed, MarkerLayer takes the
// map reference lazily via setMap() below, once initMap runs.

// Chart layers (SignalK chart-server tiles + the built-in OSM /
// OpenSeaMap entries from BuiltInCharts.cs). All flow through
// addChartLayer so they share the lifecycle + opacity stacking.
const chartLayers = new MarkerLayer();  // keyed by chart identifier

// Zoom-level badge (bottom-right). Assigned in initMap so the control
// exists before the first zoomend fires.
let zoomBadge = null;

// "Nice round" nautical-mile values for the corner range scale chip
// AND the pinch-zoom preview. Single source of truth so the bar in
// the corner and the chip floating mid-gesture pick the same step --
// previously this lived as a duplicated literal in both places. Same
// ladder every commercial plotter uses (Garmin / B&G / Raymarine).
const RANGE_SCALE_NM_LADDER = [
    0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 20, 50, 100, 200, 500
];

// How long the pinch-zoom preview chip lingers AFTER zoomend before
// fading out. The 0.4s opacity transition on `.ona-zoom-preview` runs
// AFTER this timer expires, so total visible time is LINGER + 400ms.
const PINCH_PREVIEW_LINGER_MS = 700;

/**
 * Pure helper: pick a nice-round nautical-mile value at or below the
 * sample length and the matching pixel width to render. Used by the
 * permanent range-scale chip and the pinch-zoom preview. Caps the
 * pixel width at the sample size so a sub-ladder zoom (tighter than
 * 0.02 nm visible per 200 px sample) doesn't render a bar wider than
 * the sample it represents.
 *
 * @param map Leaflet map
 * @param sampleHalfPx pixels each side of centre to measure
 * @param minPx       floor for the visible bar so it stays scannable
 * @returns {{label: string, widthPx: number}}
 */
function computeNiceScale(map, sampleHalfPx, minPx) {
    const c = map.getCenter();
    const cp = map.latLngToContainerPoint(c);
    const lhs = map.containerPointToLatLng([cp.x - sampleHalfPx, cp.y]);
    const rhs = map.containerPointToLatLng([cp.x + sampleHalfPx, cp.y]);
    const meters = lhs.distanceTo(rhs);
    const totalNm = meters / 1852;
    let nice = RANGE_SCALE_NM_LADDER[0];
    for (const v of RANGE_SCALE_NM_LADDER) { if (v <= totalNm) nice = v; }
    // Width clamp: bar can't exceed the sample width (would be a lie),
    // and floors at minPx so it stays visible at very wide zooms.
    const sampleWidthPx = 2 * sampleHalfPx;
    const rawWidth = Math.round(sampleWidthPx * (nice / totalNm));
    const widthPx = Math.min(sampleWidthPx, Math.max(minPx, rawWidth));
    // Trim trailing zeros and the orphan dot for sub-1 nm values
    // ("0.50 nm" reads as fake precision; "0.5 nm" is what a
    // chartplotter shows). >= 1 stays integer.
    const label = nice >= 1
        ? `${nice} nm`
        : `${nice.toFixed(2).replace(/0+$/, '').replace(/\.$/, '')} nm`;
    return { label, widthPx };
}

// Wake-lock code lives in platform/wakeLock.js so it can
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

// Range-scale chip. Draws a horizontal tick at a "nice round"
// nautical-mile value (..., 0.1, 0.2, 0.5, 1, 2, 5, 10, ...) so the
// helm can eyeball distance on the chart while zooming.
//
// Positioning lives in CSS (.ona-range-scale): the chip sits in the
// bottom-centre column just above the bc-stack (anchor watch /
// active route HUD card), NOT inside Leaflet's control rail. Helm
// asked for it there because the bottom-left corner felt
// disconnected from anything ("just somewhere").
//
// Visibility: only shown DURING a zoom (zoomstart -> zoomend) plus a
// short linger so the helm reads the final value before it fades.
// Earlier iteration kept the chip permanently visible AND a separate
// centred chip appeared mid-pinch; the helm reported that as
// duplicate UI. Now there's one chip, in the helm's chosen spot,
// visible exactly when it's useful (during the zoom gesture).
//
// The chip is also force-hidden via setRangeScaleHidden whenever an
// active-route HUD card occupies the same vertical slot.
let _rangeScaleEl = null;
let _rangeScaleLabelEl = null;
let _rangeScaleBarEl = null;
let _rangeScaleHideTimer = null;
function initRangeScale(mapInstance) {
    _rangeScaleEl = L.DomUtil.create('div', 'ona-range-scale',
        mapInstance.getContainer());
    _rangeScaleLabelEl = L.DomUtil.create('span', 'ona-range-scale-label',
        _rangeScaleEl);
    _rangeScaleBarEl = L.DomUtil.create('span', 'ona-range-scale-bar',
        _rangeScaleEl);
    L.DomEvent.disableClickPropagation(_rangeScaleEl);
    // Compute the value once at init so the first show carries the
    // right label even if the helm zooms before any moveend fires.
    updateRangeScale();
    mapInstance.on('zoomstart', () => {
        if (_rangeScaleHideTimer) {
            clearTimeout(_rangeScaleHideTimer);
            _rangeScaleHideTimer = null;
        }
        updateRangeScale();
        _rangeScaleEl.classList.add('visible');
    });
    mapInstance.on('zoom', updateRangeScale);
    mapInstance.on('zoomend', () => {
        updateRangeScale();
        // Linger after release so the helm reads the final value.
        // Same PINCH_PREVIEW_LINGER_MS budget the now-removed centred
        // chip used; combined with the .visible -> opacity transition
        // it gives ~700 ms of stable read + 400 ms fade.
        if (_rangeScaleHideTimer) clearTimeout(_rangeScaleHideTimer);
        _rangeScaleHideTimer = setTimeout(
            () => _rangeScaleEl.classList.remove('visible'),
            PINCH_PREVIEW_LINGER_MS);
    });
}
function updateRangeScale() {
    if (!_rangeScaleEl || !map) return;
    const { label, widthPx } = computeNiceScale(map, 100, 8);
    _rangeScaleLabelEl.textContent = label;
    _rangeScaleBarEl.style.width = `${widthPx}px`;
    _rangeScaleEl.title = `Range: ${label}`;
}

/**
 * Hide the range-scale chip while another HUD card occupies the
 * bottom-centre slot (active route during navigation). Toggled from
 * C# on setActiveRoute / clearActiveRoute. Idempotent. Safe to call
 * before initRangeScale -- the no-op early-return covers the bootstrap
 * window.
 */
export function setRangeScaleHidden(hidden) {
    if (!_rangeScaleEl) return;
    _rangeScaleEl.classList.toggle('ona-range-scale-hidden', !!hidden);
}

// Routes and server track.
const routeLayers = new MarkerLayer();  // keyed by route ID
let serverTrackLayer = null;

// Active route + course line state lives in activeRouteLayer.js +
// courseLineLayer.js. The mux still consults
// activeRouteLayerMod.isOverlayHidden() inside applyFrame because the
// frame batcher decides whether to redraw the course line on each tick.

// Layline state lives in laylineLayer.js.

// MOB state lives in mobLayer.js.

// Anchor watch state lives in anchorLayer.js.

// Own-vessel state lives in the layer modules now (aisLayer for CPA,
// anchorLayer / mobLayer / measureLayer for their own geometry).
// boatMarker still stashes its data on _onaSelfData for popup rendering.

// HTML-escape untrusted strings for popup content. Module-level
// alias so the in-file template literals don't have to drag the
// 'Imported' suffix through every interpolation.
const esc = escImported;
// Mux-side wireDeleteConfirm passes the cached dotNetRef through;
// modules that own their own popups call the imported helper directly
// with their own getDotNetRef.
const wireDeleteConfirm = (popup, selector, method, id) =>
    wireDeleteConfirmImported(popup, selector, method, id, () => dotNetRef);

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
    waypoint: '#c76f51',      // --ann-waypoint
    note:     '#c8892e',      // --ann-note
    region:   '#d4a850',      // --ann-region
    regionFill:'rgba(212, 168, 80, 0.18)', // --ann-region-fill
    measure:  '#e2e8f0',      // --map-measure
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
    MapColors.waypoint   = pick('--ann-waypoint',   MapColors.waypoint);
    MapColors.note       = pick('--ann-note',       MapColors.note);
    MapColors.region     = pick('--ann-region',     MapColors.region);
    MapColors.regionFill = pick('--ann-region-fill', MapColors.regionFill);
    MapColors.measure    = pick('--map-measure',    MapColors.measure);
}

// selfIcon is rebuilt on each initMap() call so a fresh palette read
// is reflected. Previously it was a module-level const built before
// CSS had a chance to load, which locked the magenta hex even after
// the stylesheet defined a new --map-own.
let selfIcon = null;

// AIS / radar / SART icons + caches moved into aisLayer.js (sole user).
// makeBoatSvg + shipTypeGlyph + makeIcon stay above for the own-boat
// marker built in initMap(). aisLayer keeps its own copies because the
// chart's own-boat is logically a separate concern from AIS targets;
// keeping the helpers small + duplicated avoids a tight coupling
// between the modules just to share three SVG templates.

// mobIcon moved into mobLayer.js (sole user).

function rotateMarker(marker, rad) {
    if (rad == null) return;
    const el = marker.getElement();
    if (!el) return;
    const svg = el.querySelector('svg');
    if (svg) svg.style.transform = `rotate(${rad * DEG}deg)`;
}

// ========== EXPORTED FUNCTIONS ==========

export function initMap(elementId, lat, lon, zoom, dotNetObjRef, slowClient) {
    if (map) map.remove();
    dotNetRef = dotNetObjRef;

    // Pull the --map-* palette out of the stylesheet now that it's
    // parsed, then build the own-boat icon from the refreshed value.
    // Doing this inside initMap (instead of at module load) means a
    // palette tweak applied via :root takes effect without touching
    // the JS -- the one edit site is app.css.
    readMapColors();
    selfIcon = makeIcon(makeBoatSvg(MapColors.own, 30, true), 30);

    // The slow-client flag is detected on the C# side
    // (ClientCapabilitiesService) and passed in here. Coercing to a
    // bool in case Blazor's interop hands us undefined for an older
    // C# call site that hasn't been updated yet -- in that case we
    // fall back to the desktop-class default of false.
    isSlowClient = !!slowClient;

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
    // Map maxZoom is bumped to native + ChartUpscale.MaxLevels (= 22)
    // so the chart-upscale decorator can actually upscale tiles
    // past the base layers' native cap. The map's view-zoom is the
    // ceiling for what L.tileLayer.maxZoom can deliver, and a
    // hardcoded 19 here would silently cap the upscale feature
    // before the decorator even runs (the symptom the helm reported
    // as "overzoom doesn't work"). OSM / OpenSeaMap (synthesised in
    // OnaPlotter/Utilities/BuiltInCharts.cs) deliberately ship with
    // AllowUpscale=false and MaxZoom=19, so they cap at native and
    // go blank past 19 -- this is intentional, so the upscaled SK
    // chart on top dominates and a frame-late basemap upscale
    // doesn't compete with it (helm field-tested as flicker).
    //
    // Removable contract for ChartUpscale: revert this to 19 if
    // the feature is ripped out (the constant is part of the 6
    // surfaces listed in OnaPlotter/Utilities/ChartUpscale.cs).
    map = L.map(elementId, {
        zoomControl: false,
        maxZoom: 19 + 3,         // 3 == ChartUpscale.MaxLevels (mirrored in C#)
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
    // chartLayers + routeLayers MarkerLayers stay in the mux; waypoint /
    // note / region / aton layers each own their own MarkerLayer inside
    // their dedicated module and call setMap on it from their init().
    for (const ml of [chartLayers, routeLayers]) {
        ml.setMap(map);
    }

    // Per-feature module init. Each module receives the freshly-built
    // Leaflet map plus any cross-module deps it needs to read at update
    // time (palette, edit-mode flags, helper functions). Modules cache
    // these refs internally; their public update / clear functions are
    // re-exported below so the C#-side InvokeVoidAsync calls work
    // unchanged.
    weatherLayerMod.init(map);
    anchorLayerMod.init(map, { colors: MapColors });
    mobLayerMod.init(map, { colors: MapColors });
    laylineLayerMod.init(map, { colors: MapColors });
    atonLayerMod.init(map);
    measureLayerMod.init(map, { colors: MapColors, pointToSegmentPixels });
    routeEditLayerMod.init(map, { colors: MapColors, pointToSegmentPixels });
    polygonEditLayerMod.init(map);
    // Edit-mode flags + the mode-specific "add point" dispatcher are
    // shared by waypoint / note / region / aisLayer / activeRouteLayer
    // (per-line click handlers fall back into route / polygon / measure
    // flows when the helm is in those modes). Built once here and
    // passed into every init so each module gets the same fresh-read
    // shape.
    //
    // Declared BEFORE the waypoint / note / region inits below: those
    // three spread `...editModeDeps` into their options bag, and `const`
    // is in the temporal dead zone until its line executes. The
    // previous order ("declare after the spreads, hoping function-scope
    // hoisting saves us") threw ReferenceError at runtime -- `var`
    // would have hoisted, but `const` does not, and a freshly-loaded
    // page crashed the whole map render.
    const editModeDeps = {
        getEditModeFlags: () => ({
            routeEdit: routeEditLayerMod.isActive(),
            polygonEdit: polygonEditLayerMod.isActive(),
            measure: measureLayerMod.isActive(),
        }),
        editModeAddPoint: (mode, lat, lon) => {
            if (mode === 'route') addEditWaypoint(lat, lon);
            else if (mode === 'polygon') addPolygonVertexInternal(lat, lon);
            else measureLayerMod.addMeasurePoint(lat, lon);
        },
    };
    waypointLayerMod.init(map, { colors: MapColors, getDotNetRef: () => dotNetRef, ...editModeDeps });
    noteLayerMod.init(map, { colors: MapColors, getDotNetRef: () => dotNetRef, ...editModeDeps });
    regionLayerMod.init(map, { colors: MapColors, getDotNetRef: () => dotNetRef, ...editModeDeps });
    aisLayerMod.init(map, {
        colors: MapColors,
        isSlowClient,
        getDotNetRef: () => dotNetRef,
        ...editModeDeps,
        getOwnMmsi: () => ownMmsi,
        flagUrl,
        rotateMarker,
    });
    courseLineLayerMod.init(map, { colors: MapColors });
    activeRouteLayerMod.init(map, {
        colors: MapColors,
        getDotNetRef: () => dotNetRef,
        ...editModeDeps,
        buildActiveRoutePopupHtml,
        wireRouteDeactivate,
        wireRouteEdit,
        wireDeleteConfirm,
        routeTotalNauticalMiles,
        clearCourseLine: () => courseLineLayerMod.clearCourseLine(),
    });

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

    // Range scale: instrument-styled tick + label so the helm can
    // gauge distance on the chart without poking the +/- buttons.
    // Bottom-left corner -- same Leaflet control rail as the zoom
    // badge so the two chips stack predictably even when the depth
    // HUD card moves on viewport changes.
    initRangeScale(map);

    // (The centred pinch-zoom preview chip used to live here; it was
    // a duplicate of the bottom-centre range-scale chip in a different
    // spot. The helm asked for one chip in the bottom-centre slot only,
    // visible during the zoom gesture. initRangeScale now wires the
    // zoomstart/zoom/zoomend listeners so the bottom-centre chip
    // appears + updates + lingers exactly when the helm wants it.)

    // isSlowClient was set at the top of initMap; the same flag drives
    // tile updateWhenIdle here so all perf gates decide together.
    //
    // keepBuffer bumped above the default 2 so tiles stay in memory a
    // few rings further out; panning back doesn't re-request. Cheap
    // memory, noticeable smoothness on the Pi-local-wifi setup where
    // re-fetch RTT is low but visible.
    // OSM + OpenSeaMap base layers used to be hardcoded here as
    // L.tileLayer instances with detectRetina: !isSlowClient. They
    // now flow through addChartLayer like any other chart, synthesised
    // on the C# side via OnaPlotter/Utilities/BuiltInCharts.cs and
    // seeded as enabled on first run from Map.razor. The helm gets
    // uniform control (toggle, reorder, opacity stacking) over basemap
    // and SignalK charts. Tile URLs still point at the public internet
    // endpoints so the boat doesn't need to host basemaps. detectRetina
    // (which fetches {z+1} tiles for high-DPI devices, gated on
    // !isSlowClient to avoid 4x tile fetches on a Pi) is applied per-
    // layer inside addChartLayer.

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
        if (measureLayerMod.isActive()) {
            boatMarker.closePopup();
            measureLayerMod.addVesselMeasurePoint();
            return;
        }
        const data = boatMarker._onaSelfData || {};
        boatMarker.setPopupContent(buildSelfPopupHtml(data));
    });
    // Own COG vector -- matches the AIS COG vector style (weight 1.5,
    // dash 6/4) but in MapColors.own (the boat's identity colour, same
    // chevron palette) instead of the lighter MapColors.cogVector. Helm
    // wants a single visual language: own + AIS vectors look the same,
    // colour disambiguates whose. Tip dot below mirrors AIS's
    // aisVectorTips so both share the "boat is here at +VECTOR_MINUTES"
    // landmark.
    boatVector = L.polyline([], {
        color: MapColors.own, weight: 1.5, dashArray: '6,4', opacity: 0.85,
    }).addTo(map);

    // Map click: in route edit mode, add waypoint. In measurement
    // mode, drop a measurement point. Otherwise just dismiss menus.
    //
    // Priority: measure FIRST, then route-edit, then polygon-edit.
    // Helm asked for this after testing route-edit + measure
    // simultaneously: every click landed on the route, and the
    // ruler couldn't be extended without ending route-edit. Now an
    // active measurement always wins the click; route-edit stays
    // open in the background and resumes accepting clicks once
    // measure stops.
    map.on('click', (e) => {
        if (measureLayerMod.isActive()) {
            // Segment-click insertion (insertMeasurePointOnSegment)
            // bubbles into this handler immediately after splicing the
            // new vertex; without the suppression flag we'd then append
            // a duplicate point at the end of the ruler. Same pattern
            // as routeEditSuppressNextMapClick for route edit.
            if (measureLayerMod.consumeSuppressNextMapClick()) return;
            measureLayerMod.addMeasurePoint(e.latlng.lat, e.latlng.lng);
            return;
        }
        if (routeEditLayerMod.isActive()) {
            // L.DomEvent.stopPropagation on the polyline click
            // doesn't actually stop Leaflet's map-level click dispatch
            // (different event channels), so a leg-click fires
            // insertEditVertexOnSegment AND then the map-click would
            // also append the same point at the end. The segment
            // handler sets a short-lived suppression flag; we honour
            // it here.
            if (routeEditLayerMod.consumeSuppressNextMapClick()) return;
            addEditWaypoint(e.latlng.lat, e.latlng.lng);
            return;
        }
        if (polygonEditLayerMod.isActive()) {
            addPolygonVertexInternal(e.latlng.lat, e.latlng.lng);
            return;
        }
        if (dotNetRef) dotNetRef.invokeMethodAsync('OnDismissContextMenu').catch(() => {});
    });

    // Right-click (desktop) and long-press (touch) -> context menu callback to Blazor.
    function showContextMenu(latlng) {
        if (routeEditLayerMod.isActive() || polygonEditLayerMod.isActive() || !dotNetRef) return;
        // In measure mode the right-click / long-press gesture means
        // "reset the current measurement" rather than "open the create-
        // here menu". Wipe the points and stay in measure mode so the
        // helm can immediately start a fresh measurement; opening the
        // context menu over a half-built ruler would just be in the way.
        if (measureLayerMod.isActive()) {
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
        if (boatLat != null && boatLon != null && !activeRouteLayerMod.isOverlayHidden()) {
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

    // Push the new boat fix into aisLayer.js (CPA prediction needs
    // own-boat lat/lon/COG/SOG; the guard-zone ring chases the boat).
    aisLayerMod.setBoatPosition(lat, lon, cogRad, sogMs);
    // Push the new boat fix into anchorLayer.js so it can sample the
    // swing-arc trail, recolour the alarm circle, and re-anchor the
    // boat<->anchor line in one place.
    anchorLayerMod.setBoatPosition(lat, lon);

    const end = vectorEnd(lat, lon, cogRad, sogMs);
    if (end) {
        boatVector.setLatLngs([[lat, lon], end]);
        // Tip dot at the vector end -- mirrors aisLayer's vessel
        // vector tip so own + AIS vectors share the same visual
        // landmark ("boat will be here at +VECTOR_MINUTES"). Same
        // shape: filled circle, MapColors.own, non-interactive.
        if (boatVectorTip) {
            boatVectorTip.setLatLng(end);
        } else {
            boatVectorTip = L.circleMarker(end, {
                radius: 2.5,
                color: MapColors.own,
                fillColor: MapColors.own,
                fillOpacity: 1,
                weight: 1,
                interactive: false,
            }).addTo(map);
        }
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
        if (boatVectorTip) { map.removeLayer(boatVectorTip); boatVectorTip = null; }
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

    // Push the new boat fix into mobLayer.js so the boat<->MOB line
    // and bearing/distance label re-anchor when a MOB is active.
    mobLayerMod.setBoatPosition(lat, lon);

    // (Anchor-watch alarm-state evaluation moved into anchorLayerMod.setBoatPosition above.)

    // Push the new boat fix into measureLayer.js so vessel-anchored
    // segments redraw against fresh own-boat coords. Internally it
    // skips when no measurement is active or none of the points is
    // vessel-anchored.
    measureLayerMod.setBoatPosition(lat, lon);
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
    // C# rate-limits track-segment emission to once per 5 s
    // (MapFrameBuilder.TrackEmitIntervalMs), so the JS batch buffer
    // is no longer protecting against interop overhead. Flush
    // immediately so the helm sees the trail extend on each emit
    // rather than waiting for 10 segments (~50 s) to accumulate.
    pendingTrackPoints.push([lat, lon, sogMs, prevLat, prevLon]);
    flushTrackPoints();
}

// 1000 segments × 5 s emit cadence = ~83 min of visible history,
// matching the C# TrackBuffer capacity. Sized to cover a typical
// day-sail without the trail wraparound the helm previously saw on
// bursty multi-Hz feeds (when the cap was hit in 1-2 min).
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

// --- AIS targets / guard zone / harbor mode ---
// Implementation in aisLayer.js; mux re-exports the C# entries.
export const updateAisTargets = (vessels) => aisLayerMod.updateAisTargets(vessels);
export const focusVessel = (context) => aisLayerMod.focusVessel(context);
export const setGuardZone = (radiusNm, lookaheadMin, warningFactor) =>
    aisLayerMod.setGuardZone(radiusNm, lookaheadMin, warningFactor);
export const setGuardZoneVisible = (visible) =>
    aisLayerMod.setGuardZoneVisible(visible);
export const setGuardZoneWarningRingVisible = (visible) =>
    aisLayerMod.setGuardZoneWarningRingVisible(visible);
export const setHarborMode = (enabled) => aisLayerMod.setHarborMode(enabled);

// --- Persistent measurement tool ---
// Implementation in measureLayer.js; mux re-exports the C# entries.
export const setMeasureMode = (active) => measureLayerMod.setMeasureMode(active);
export function clearMeasure() { return measureLayerMod.clearMeasure(); }
export const measureFromVesselTo = (lat, lon) => measureLayerMod.measureFromVesselTo(lat, lon);

// --- MOB ---

// Implementation in mobLayer.js; mux re-exports the C# entries.
export const setMob = (lat, lon) => mobLayerMod.setMob(lat, lon);
export const clearMob = () => mobLayerMod.clearMob();

// --- Anchor Watch ---
// Implementation in anchorLayer.js; mux re-exports the C# entries.
export const setAnchor = (lat, lon, radiusM) => anchorLayerMod.setAnchor(lat, lon, radiusM);
export const clearAnchor = () => anchorLayerMod.clearAnchor();
export const setAnchorRaising = (raising) => anchorLayerMod.setAnchorRaising(raising);
export const updateAnchorRadius = (radiusM) => anchorLayerMod.updateAnchorRadius(radiusM);

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
// Overzoom is applied via withOverzoom() below: the decorator bumps
// the layer's maxZoom past maxNativeZoom by ChartUpscale.Effective
// levels so Leaflet GPU-upscales the last-fetched tile instead of
// going blank. Native zoom is preserved so the actual fetch budget
// stays at the chart's real tile pyramid.
//
// A tileerror-driven downshift calibrator (see chartZoomErrors and
// the layer.on('tileerror', ...) handler below) handles the case
// where a chart's metadata over-declares maxzoom: after N 404s at
// the current cap, drop maxNativeZoom by 1 and redraw. Idempotent --
// once we drop to the chart's actual native zoom, errors stop.
//
// Per-chart tile-error counter, surfaced via getChartTileErrors() for
// the Settings dev section. Counts the `tileerror` Leaflet event for
// each chart layer. Helm-facing diagnostic; also drives the
// downshift-on-404 calibrator below.
const chartTileErrors = new Map();   // id -> total count

// Per-chart per-zoom error tally for the downshift calibrator. When
// N errors land at the layer's current `maxNativeZoom`, we drop the
// cap by 1 and redraw -- the helm's chart server claims a maxzoom
// the tile pyramid doesn't actually reach (common with MBTiles
// providers that publish the requested-build maxzoom even when the
// build truncated earlier). v1 of the overzoom feature avoided
// runtime probes precisely because mixed signals (404 vs 200-empty
// vs redirect) made the calibrator unstable; this iteration only
// listens to bonafide `tileerror` events (the 404 case) and only
// downshifts when the error is AT the current cap, so the fix is
// idempotent: once we drop to the chart's actual native zoom,
// errors stop and the calibrator goes quiet.
const chartZoomErrors = new Map();   // id -> Map<zoom, count>
// Threshold + decision logic live in chartDownshift.js so they can
// be unit-tested without a Leaflet TileLayer instance. The handler
// below stays thin: read state, call decideDownshift, apply
// side-effects on a downshift result.

export function addChartLayer(id, tileUrl, minZoom, maxZoom, opacity, bounds, upscaleLevels, attribution) {
    if (!map || chartLayers.has(id)) return false;
    // OSM + OpenSeaMap used to be hardcoded as Leaflet base layers
    // attached at initMap time. They now flow through this function
    // like any other chart, synthesised on the C# side via
    // OnaPlotter/Utilities/BuiltInCharts.cs. The helm can toggle them
    // off, reorder them, and they participate in the same opacity
    // stacking + chart-upscale opt-out that SignalK-served charts do.
    // Tile URLs still point at the public internet endpoints for OSM
    // and OpenSeaMap so the boat doesn't need to host basemaps.
    //
    // Defensive numeric validation: a hostile or buggy SK chart
    // provider sending negative or NaN MaxZoom passes the C#-side
    // ?? 18 fallback (which only catches null) but would land here
    // as a truthy negative. The `|| 18` falsy idiom doesn't catch
    // it either. Force a positive finite int; otherwise the layer
    // renders nothing and the calibrator's `native <= minZ + 1`
    // floor misfires.
    const native = (Number.isFinite(maxZoom) && maxZoom > 0) ? (maxZoom | 0) : 18;
    const minZ = (Number.isFinite(minZoom) && minZoom > 0) ? (minZoom | 0) : 1;
    let opts = {
        minZoom: minZ,
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
        // Attribution: SK chart-server tiles ship empty (the SK server
        // doesn't preach about chart sources), but the built-in OSM /
        // OpenSeaMap synthesised charts pass the ODbL / CC-BY-SA
        // licence string through here so Leaflet's bottom-right
        // attribution control surfaces it whenever the layer is on.
        attribution: attribution || '',
        // referrerPolicy: matches what the previous hardcoded
        // osmBaseLayer / seaBaseLayer used so the OSM tile servers
        // see only the origin (not the full referrer URL) -- some
        // tile providers reject when the referrer carries a path
        // they don't whitelist.
        referrerPolicy: 'strict-origin-when-cross-origin',
        errorTileUrl: ''  // Suppress broken tile images for out-of-bounds requests.
    };
    // Constrain tile requests to the chart's coverage area. A SK
    // provider sending [NaN, NaN, NaN, NaN] or [Infinity, ...] or
    // out-of-range lat/lon would otherwise reach L.latLngBounds and
    // produce a degenerate object whose tile-intersection check
    // returns false for everything -- silent blank chart. Match the
    // sanity range LoadMapView uses (-90 <= lat <= 90; lon clamped
    // generously since wrap-around mid-pacific charts are real).
    if (bounds && bounds.length === 4 &&
        bounds.every(Number.isFinite) &&
        bounds[1] >= -90 && bounds[1] <= 90 &&    // south
        bounds[3] >= -90 && bounds[3] <= 90 &&    // north
        bounds[0] >= -540 && bounds[0] <= 540 &&  // west
        bounds[2] >= -540 && bounds[2] <= 540) {  // east
        opts.bounds = L.latLngBounds(
            [bounds[1], bounds[0]],  // SW: [south, west]
            [bounds[3], bounds[2]]   // NE: [north, east]
        );
    }
    // Chart-upscale decorator (overzoom). Wraps options before the
    // Leaflet constructor; 0 levels = identity passthrough. Removable
    // contract: drop the import + this line + the upscaleLevels
    // parameter and the feature is gone.
    opts = withOverzoom(opts, upscaleLevels | 0);
    const layer = L.tileLayer(tileUrl, opts);
    layer.addTo(map);
    layer.setZIndex(50);
    chartLayers.set(id, layer);
    chartTileErrors.set(id, 0);
    chartZoomErrors.set(id, new Map());
    layer.on('tileerror', (ev) => {
        // A late tileerror after removeChartLayer(id) would otherwise
        // resurrect the deleted entry via the `?? 0` fallback and
        // grow chartTileErrors unboundedly across many add/remove
        // cycles. Guard with .has(id) so a removed chart's in-flight
        // <img>.error events stop being counted.
        if (!chartTileErrors.has(id)) return;
        chartTileErrors.set(id, chartTileErrors.get(id) + 1);
        // Downshift calibrator. Pure decision logic in chartDownshift.js;
        // this handler reads layer state, asks decideDownshift what to
        // do, then applies side-effects. Refactor preserves the
        // historical behaviour (idempotent, only acts at the cap, only
        // on bonafide tileerror events) -- pinned by chartDownshift.test.js.
        const zoomMap = chartZoomErrors.get(id);
        if (!zoomMap) return;
        const z = ev?.coords?.z;
        const native = layer.options?.maxNativeZoom;
        const minZ = layer.options.minZoom ?? 1;
        const priorErrorsAtZ = (typeof z === 'number') ? (zoomMap.get(z) ?? 0) : 0;
        const decision = decideDownshift({
            errorZ: z, currentNative: native, minZoom: minZ, priorErrorsAtZ
        });
        if (decision.reason === 'bad-coords' || decision.reason === 'bad-native' ||
            decision.reason === 'not-at-cap') {
            return;
        }
        if (decision.shouldDownshift) {
            layer.options.maxNativeZoom = decision.newNative;
            // Reset the per-zoom counter for the OLD cap so the next
            // downshift (if the metadata was off by 2+) starts counting
            // fresh against the NEW cap.
            zoomMap.delete(z);
            layer.redraw();
            console.warn(
                `[chart] ${id}: declared maxzoom ${native} returns 404; ` +
                `downshifted maxNativeZoom to ${decision.newNative}`);
        } else {
            zoomMap.set(z, decision.nextErrorsAtZ);
        }
    });
    restackChartOpacities();
    return true;
}

/**
 * Diagnostic getter for the Settings dev section. Returns a flat
 * object { chartId: errorCount } so a Razor binding can render a
 * simple list. Helms see "Chart X: 47 missing tiles" and decide
 * whether to lower its upscale cap or report a server-side bug.
 *
 * Cumulative-since-add: each entry counts errors from when the
 * layer was added (chartTileErrors.set(id, 0) inside addChartLayer)
 * until removeChartLayer drops the entry. Toggling the chart off
 * and on resets the count. There is no time window; a multi-day
 * session reports the lifetime total. Helms reading "47" should
 * interpret it as "since this chart was last enabled."
 */
export function getChartTileErrors() {
    const out = {};
    for (const [id, count] of chartTileErrors) out[id] = count;
    return out;
}

/**
 * Reset a single chart's tile-error counter without removing the
 * layer. Used by the Settings dev section "reset" button so the
 * helm can re-baseline a long-running session without toggling the
 * layer (which would force a tile re-fetch and visual flicker).
 * No-op if the id has no entry; the caller doesn't need to know
 * whether the chart is currently enabled.
 */
export function resetChartTileErrors(id) {
    if (chartTileErrors.has(id)) chartTileErrors.set(id, 0);
}

export function removeChartLayer(id) {
    // Stop the layer's tileerror handler before MarkerLayer.remove
    // detaches it. Leaflet doesn't unbind layer-level listeners on
    // _onRemove, and an in-flight <img> that 404s after detach would
    // otherwise still fire the handler -- the .has(id) guard inside
    // the handler defends against the resulting deleted-then-re-set
    // entry, but turning the dispatch off entirely is the simpler
    // belt-and-suspenders.
    const layer = chartLayers.get(id);
    if (layer && typeof layer.off === 'function') layer.off('tileerror');
    chartLayers.remove(id);
    chartTileErrors.delete(id);
    chartZoomErrors.delete(id);
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
    const popupOptions = { className: 'route-popup', maxWidth: 320, autoClose: true };
    const popupHtml = () => buildRoutePopupHtml(id, name, coords.length, nmTotal);
    line.bindPopup(popupHtml(), popupOptions);
    hitLine.bindPopup(popupHtml(), popupOptions);

    const onLineClick = (ev, sourceLine) => {
        if (routeEditLayerMod.isActive() || polygonEditLayerMod.isActive() || measureLayerMod.isActive()) {
            L.DomEvent.stopPropagation(ev);
            const ll = ev.latlng;
            if (!ll) return;
            // Measure beats route / polygon edit on a route-polyline
            // click. Mirrors the same priority on the map-level click
            // handler above; the helm's original complaint was that
            // measuring AROUND an existing route was impossible
            // because the polyline ate every click. Marker / region /
            // active-route polyline clicks deliberately use the
            // historical route-first order -- those targets are
            // unambiguous (the helm tapped a specific item) so the
            // measure-first override is scoped to the empty-map and
            // stale-route polyline cases only.
            if (measureLayerMod.isActive())            measureLayerMod.addMeasurePoint(ll.lat, ll.lng);
            else if (routeEditLayerMod.isActive())     addEditWaypoint(ll.lat, ll.lng);
            else if (polygonEditLayerMod.isActive())   addPolygonVertexInternal(ll.lat, ll.lng);
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
// C# which flips routeEditLayerMod.isActive() on and loads the polyline into the
// edit layer. Closes the popup immediately so a second tap doesn't
// land on a now-invisible button (the edit toolbar takes over the
// viewport once routeEditLayerMod.isActive() flips).
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

// Live ETA cache pushed from C# whenever ActiveRouteTimeToGo changes.
// We store BOTH the value and the wall-clock at push time so the
// popup can recompute the live remaining time from elapsed clock
// regardless of whether C# has pushed an update recently. Without
// the timestamp the helm sees a frozen "in 60m" 30 minutes into a
// 60-minute leg, because C#-side SyncActiveRouteAsync only pushes
// when the leg / waypoint / pointIndex actually changes.
let _activeRouteTtgSeconds = null;
let _activeRouteTtgPushedAtMs = 0;
export function setActiveRouteTtgSeconds(seconds) {
    if (typeof seconds === 'number' && seconds > 0) {
        _activeRouteTtgSeconds = seconds;
        _activeRouteTtgPushedAtMs = Date.now();
    } else {
        _activeRouteTtgSeconds = null;
        _activeRouteTtgPushedAtMs = 0;
    }
}

// Format seconds into a wall-clock ETA + "in Xh Ym" / "in Xm" pair.
// Returns null when ttg is null / non-positive so the popup can drop
// the row entirely instead of showing "ETA --". For >99h the
// parenthetical collapses to "(in >99h)" rather than lying with a
// truncated "(in 99h 59m)" -- that ambiguity surfaced in code
// review. Realistically the helm doesn't sit on a >4-day leg
// without an intermediate waypoint, but the contract holds.
function formatRouteEta(ttgSeconds) {
    if (ttgSeconds == null || !isFinite(ttgSeconds) || ttgSeconds <= 0) return null;
    const arrivalMs = Date.now() + ttgSeconds * 1000;
    const arrival = new Date(arrivalMs);
    const hh = arrival.getHours().toString().padStart(2, '0');
    const mm = arrival.getMinutes().toString().padStart(2, '0');
    const totalMin = Math.max(1, Math.round(ttgSeconds / 60));
    const totalH = Math.floor(totalMin / 60);
    let inText;
    if (totalH > 99) {
        inText = '>99h';
    } else if (totalH > 0) {
        inText = `${totalH}h ${totalMin % 60}m`;
    } else {
        inText = `${totalMin}m`;
    }
    return `ETA ${hh}:${mm} (in ${inText})`;
}

// Read the live TTG, decremented by the elapsed wall-clock since
// the last C# push. This is what the popup actually wants -- a
// stale-from-30-minutes-ago cache returns the right answer because
// we subtract the elapsed time. Returns null when no value has
// ever been pushed or the decrement crossed zero (boat arrived).
function liveActiveRouteTtgSeconds() {
    if (_activeRouteTtgSeconds == null) return null;
    const elapsed = (Date.now() - _activeRouteTtgPushedAtMs) / 1000;
    const remaining = _activeRouteTtgSeconds - elapsed;
    return remaining > 0 ? remaining : null;
}

// Active-route popup: same shape as the regular-route popup but the
// primary action is "Deactivate" (clear the SignalK course) rather
// than "Activate". Edit + Delete keep working on the route resource
// via the same JSInvokables the regular-route popup wires up.
// ETA line is rendered when the cached _activeRouteTtgSeconds is
// non-null; computed at popup-open time so the helm sees a fresh
// arrival estimate without paying for a re-render on every tick.
function buildActiveRoutePopupHtml(id, name, wpCount, nmTotal) {
    const safeName = esc(name || `Route ${id.substring(0, 6)}`);
    const etaText = formatRouteEta(liveActiveRouteTtgSeconds());
    const etaRow = etaText ? `<div class="route-popup-eta">${etaText}</div>` : '';
    return `
        <div class="route-popup-body">
            <div class="route-popup-title">${safeName}</div>
            <div class="route-popup-meta">${wpCount} WP &middot; ${nmTotal.toFixed(1)} nm &middot; active</div>
            ${etaRow}
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
//
// Shows the server-side historical track. Two modes:
//   * full set         (clipToBounds = false): draws every coord in
//     one polyline, regardless of where the map is panned. Default.
//   * within bounds    (clipToBounds = true): draws only coords inside
//     the current map.getBounds() at the moment of render. A moveend
//     listener re-clips on pan / zoom so the helm sees only the
//     subset of the track that's "on screen now". The full coord set
//     stays cached on _serverTrackCoords so the re-clip doesn't have
//     to re-fetch.
//
// "w/o loading anything" (helm phrasing): the bounds-mode filter
// works on the already-loaded coord array; switching it on / off
// or panning the map never triggers a new HTTP request.
let _serverTrackCoords = null;
let _serverTrackClipToBounds = false;
let _serverTrackMoveHandler = null;

export function setServerTrack(coords, clipToBounds) {
    clearServerTrack();
    _serverTrackCoords = (coords && coords.length > 0) ? coords : null;
    _serverTrackClipToBounds = !!clipToBounds;
    if (!map || !_serverTrackCoords) return;
    _renderServerTrack();
    _ensureServerTrackMoveHandler();
}

// Toggle the within-bounds filter without re-loading coords. Helm
// flips the checkbox in the Layers panel; the cached coord array is
// re-used and the polyline is redrawn with / without the bounds clip.
export function setServerTrackClipToBounds(enabled) {
    _serverTrackClipToBounds = !!enabled;
    if (!map) return;
    _renderServerTrack();
    _ensureServerTrackMoveHandler();
}

function _renderServerTrack() {
    if (serverTrackLayer && map) { map.removeLayer(serverTrackLayer); serverTrackLayer = null; }
    if (!map || !_serverTrackCoords) return;
    let coords = _serverTrackCoords;
    if (_serverTrackClipToBounds) {
        const b = map.getBounds();
        coords = coords.filter(c => b.contains(c));
        if (coords.length === 0) return;
    }
    serverTrackLayer = L.polyline(coords, {
        color: '#94a3b8', weight: 2, opacity: 0.5
    }).addTo(map);
}

function _ensureServerTrackMoveHandler() {
    if (!map) return;
    // Always-on moveend listener while a track is loaded; it's a no-op
    // when clipToBounds is false. Lighter than re-binding on every
    // toggle (which would race a moveend mid-frame).
    if (_serverTrackMoveHandler || !_serverTrackCoords) return;
    _serverTrackMoveHandler = () => {
        if (_serverTrackClipToBounds && _serverTrackCoords) _renderServerTrack();
    };
    map.on('moveend', _serverTrackMoveHandler);
}

export function clearServerTrack() {
    if (serverTrackLayer && map) { map.removeLayer(serverTrackLayer); serverTrackLayer = null; }
    if (_serverTrackMoveHandler && map) {
        map.off('moveend', _serverTrackMoveHandler);
        _serverTrackMoveHandler = null;
    }
    _serverTrackCoords = null;
    _serverTrackClipToBounds = false;
}

// --- Active Route Navigation + course line ---
// Implementations in activeRouteLayer.js + courseLineLayer.js; mux
// re-exports the C# entries.
export const setActiveRoute = (coords, wpIdx, routeId, routeName) =>
    activeRouteLayerMod.setActiveRoute(coords, wpIdx, routeId, routeName);
export const clearActiveRoute = () => activeRouteLayerMod.clearActiveRoute();
export const setActiveOverlayHidden = (hidden) => activeRouteLayerMod.setActiveOverlayHidden(hidden);
export function setCourseLine(boatLat, boatLon, wpLat, wpLon, prevLat, prevLon, xteMeters, xteSeverity) {
    return courseLineLayerMod.setCourseLine(boatLat, boatLon, wpLat, wpLon, prevLat, prevLon, xteMeters, xteSeverity);
}
export function clearCourseLine() { return courseLineLayerMod.clearCourseLine(); }

// Stopping-state visual feedback spans both modules: dim the route
// polyline AND the course-line elements together so the helm sees
// their "Stop Navigation" tap landed without us prematurely tearing
// the overlays down (the SK delta drives the real teardown).
export function setActiveRouteStopping(stopping) {
    activeRouteLayerMod.setActiveRouteStoppingPolyline(stopping);
    courseLineLayerMod.setStoppingDim(stopping);
}

// --- Route + Polygon editing ---
// Implementations in routeEditLayer.js + polygonEditLayer.js. The
// route-edit module exports addEditWaypoint as a public function so
// the mux's map-click handler can forward taps into it; the polygon
// module mirrors that with addPolygonVertexInternal.
export const startRouteEdit = () => routeEditLayerMod.startRouteEdit();
export const stopRouteEdit = () => routeEditLayerMod.stopRouteEdit();
export const getEditRouteCoords = () => routeEditLayerMod.getEditRouteCoords();
export const undoLastEditWaypoint = () => routeEditLayerMod.undoLastEditWaypoint();
export const reverseEditRoute = () => routeEditLayerMod.reverseEditRoute();
export const removeRouteEditWaypoint = (index) => routeEditLayerMod.removeRouteEditWaypoint(index);
export const getEditRouteStats = () => routeEditLayerMod.getEditRouteStats();
export const loadRouteForEdit = (coords) => routeEditLayerMod.loadRouteForEdit(coords);
// `function` declarations rather than `const` arrows so they hoist
// to the top of the module: the call sites (line click handlers,
// drop-on-canvas dispatchers near 498/690/1344) are inside callback
// bodies that would still resolve at runtime, but ESLint's
// no-use-before-define can't see through the closure -- function
// declarations side-step the rule cleanly without disable comments
// at every call site.
function addEditWaypoint(lat, lon) { return routeEditLayerMod.addEditWaypoint(lat, lon); }

export const startPolygonEdit = () => polygonEditLayerMod.startPolygonEdit();
export const stopPolygonEdit = () => polygonEditLayerMod.stopPolygonEdit();
export const getPolygonEditCoords = () => polygonEditLayerMod.getPolygonEditCoords();
export const undoLastPolygonVertex = () => polygonEditLayerMod.undoLastPolygonVertex();
export const removePolygonEditVertex = (index) => polygonEditLayerMod.removePolygonEditVertex(index);
export const loadPolygonForEdit = (coords) => polygonEditLayerMod.loadPolygonForEdit(coords);
function addPolygonVertexInternal(lat, lon) { return polygonEditLayerMod.addPolygonVertexInternal(lat, lon); }

// Euclidean pixel distance from point p to segment ab. Used by
// routeEditLayer + measureLayer for "find the closest segment to the
// click" insertion. Kept in the mux because it's a pure helper used
// by multiple modules; passing as a dep keeps each module hermetic.
function pointToSegmentPixels(p, a, b) {
    const dx = b.x - a.x, dy = b.y - a.y;
    const len2 = dx * dx + dy * dy;
    if (len2 === 0) return Math.hypot(p.x - a.x, p.y - a.y);
    let t = ((p.x - a.x) * dx + (p.y - a.y) * dy) / len2;
    t = Math.max(0, Math.min(1, t));
    const cx = a.x + t * dx, cy = a.y + t * dy;
    return Math.hypot(p.x - cx, p.y - cy);
}

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

// --- Waypoint / Note / Region markers ---
// Implementations in waypointLayer.js, noteLayer.js, regionLayer.js.
export const addWaypointMarker = (id, lat, lon, name, createdAtIso) =>
    waypointLayerMod.addWaypointMarker(id, lat, lon, name, createdAtIso);
export const removeWaypointMarker = (id) => waypointLayerMod.removeWaypointMarker(id);

export const addNoteMarker = (id, lat, lon, title, description, createdAtIso) =>
    noteLayerMod.addNoteMarker(id, lat, lon, title, description, createdAtIso);
export const removeNoteMarker = (id) => noteLayerMod.removeNoteMarker(id);
export const clearNotes = () => noteLayerMod.clearNotes();
export const openNotePopup = (id) => noteLayerMod.openNotePopup(id);

export const addRegion = (id, rings, title, description) =>
    regionLayerMod.addRegion(id, rings, title, description);
export const removeRegion = (id) => regionLayerMod.removeRegion(id);
export const clearRegions = () => regionLayerMod.clearRegions();
export const focusRegion = (id, firstRing) => regionLayerMod.focusRegion(id, firstRing);
export const setCirclePreview = (lat, lon, radiusMeters) =>
    regionLayerMod.setCirclePreview(lat, lon, radiusMeters);
export const clearCirclePreview = () => regionLayerMod.clearCirclePreview();

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


// --- Weather Overlay ---
// Implementation in weatherLayer.js; mux re-exports the C# entries.
export const setWeatherOverlay = (tileUrl, opacity) => weatherLayerMod.setWeatherOverlay(tileUrl, opacity);
export const setWeatherOverlayOpacity = (opacity) => weatherLayerMod.setWeatherOverlayOpacity(opacity);
export const clearWeatherOverlay = () => weatherLayerMod.clearWeatherOverlay();

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
// Implementation in laylineLayer.js; mux re-exports the C# entries.
export function setLaylines(boatLat, boatLon, twdRad, twaRad, wpLat, wpLon) {
    return laylineLayerMod.setLaylines(boatLat, boatLon, twdRad, twaRad, wpLat, wpLon);
}
export const clearLaylines = () => laylineLayerMod.clearLaylines();

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
// Implementation in atonLayer.js; mux re-exports the C# entries.
export const setAtons = (atons) => atonLayerMod.setAtons(atons);
export const setAtonsVisible = (visible) => atonLayerMod.setAtonsVisible(visible);

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
    boatMarker = null; boatVector = null; boatVectorTip = null; vectorLabel = null; trackLayer = null;
    serverTrackLayer = null;
    chartLayers.clear();
    routeLayers.clear();
    aisLayerMod.dispose();
    mobLayerMod.dispose();
    anchorLayerMod.dispose();
    measureLayerMod.dispose();
    activeRouteLayerMod.dispose();
    courseLineLayerMod.dispose();
    laylineLayerMod.dispose();
    // `currentLabel` used to exist as a sibling of `currentArrow` for
    // a drift-speed tooltip on the tidal-current arrow; that label was
    // dropped but the assignment lingered here under ES module strict
    // mode, throwing ReferenceError on every dispose() and surfacing
    // as "Unhandled exception rendering component" in Blazor's error
    // boundary when the user navigated off the Chart page. Removed.
    currentArrow = null;
    weatherLayerMod.dispose();
    routeEditLayerMod.dispose();
    polygonEditLayerMod.dispose();
    waypointLayerMod.dispose();
    noteLayerMod.dispose();
    regionLayerMod.dispose();
    atonLayerMod.dispose();
    // AIS state cleanup happens in aisLayerMod.dispose() above.
    // dotNetRef is now nulled at the TOP of dispose() so map.remove()'s
    // synchronous unload handlers can't race into a half-disposed ref.
}
