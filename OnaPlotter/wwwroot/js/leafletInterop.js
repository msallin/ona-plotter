// Leaflet JS interop for the chartplotter map.
// All map state lives here; Blazor calls exported functions via IJSRuntime.

import { RAD, DEG, NM_PER_METER, VECTOR_MINUTES, SPEED_BUCKETS,
         haversineMeters, bearingDeg, destPoint, vectorEnd,
         computeCpa, speedColor, speedBucket } from './geoMath.js';

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

// Chart layers from SignalK.
const chartLayers = {};  // keyed by chart identifier
let osmBaseLayer = null;
let seaBaseLayer = null;

// Routes and server track.
const routeLayers = {};  // keyed by route ID
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
function makeBoatSvg(fill, size, isOwn) {
    const s = size || 28;
    const h = s / 2;
    // Sleek arrow shape: pointed bow, tapered stern with notch.
    const outline = isOwn
        ? `stroke="#fff" stroke-width="1.2" stroke-linejoin="round"`
        : `stroke="rgba(255,255,255,0.5)" stroke-width="0.8" stroke-linejoin="round"`;
    return `<svg width="${s}" height="${s}" viewBox="-${h} -${h} ${s} ${s}" style="filter:drop-shadow(0 1px 2px rgba(0,0,0,0.4))">
              <polygon points="0,-${h-2} ${h-6},${h-4} 0,${h-8} -${h-6},${h-4}" fill="${fill}" ${outline} opacity="${isOwn ? 1 : 0.9}"/>
            </svg>`;
}

function makeIcon(html, size) {
    return L.divIcon({ className: 'boat-icon', html, iconSize: [size, size], iconAnchor: [size/2, size/2] });
}

// Magenta stands out against the blue water on OpenSeaMap/OSM tiles and
// doesn't collide with AIS ship-type palettes (greens/blues) or the reds
// reserved for MOB and collision alarms.
const selfIcon = makeIcon(makeBoatSvg('#ec4899', 30, true), 30);

// AIS colour palette. Tuned for blue water: all warm/earth hues so
// every vessel reads clearly against OSM/OpenSeaMap tiles. Matches the
// route and waypoint colours for a coherent chart look. Any change
// here should ripple to the route/waypoint colours below so the
// palette stays in one place.
const AIS_COLORS = {
    cargo:     '#7d9b76',    // sage green - commercial bulk
    tanker:    '#c9a27e',    // warm tan - oil / liquid
    passenger: '#b589b0',    // muted plum - civilian
    fishing:   '#d4a850',    // muted gold - nets
    sailing:   '#e28862',    // warm coral - replaces sky blue that vanished on water
    pleasure:  '#f2b785',    // pale apricot - same family
    tug:       '#c0a080',    // warm beige
    military:  '#8a7a7a',    // muted brown-gray
    sar:       '#d17056',    // terracotta - "rescue orange" toned down
    default:   '#e0c9a6',    // warm cream for unclassified targets (was pale gray, muddy on water)
    danger:    '#c4453e',    // warm brick - CPA alarm
    buddy:     '#e9c46a'     // honey gold - also gets a star glyph
};

function aisColor(shipType, isDanger, isBuddy) {
    // Buddy colour overrides everything else. The collision alarm is
    // skipped for buddies in C#, so we also skip the red-danger tint
    // here to avoid the "alarm-looking but silent" mismatch.
    if (isBuddy) return AIS_COLORS.buddy;
    if (isDanger) return AIS_COLORS.danger;
    if (!shipType) return AIS_COLORS.default;
    const t = shipType.toLowerCase();
    for (const [key, color] of Object.entries(AIS_COLORS)) {
        if (t.includes(key)) return color;
    }
    return AIS_COLORS.default;
}

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

// Icon caches (one per source x colour combo).
const aisIconCache = {};
const radarIconCache = {};
function getAisIcon(color) {
    if (!aisIconCache[color]) {
        aisIconCache[color] = makeIcon(makeBoatSvg(color, 24, false), 24);
    }
    return aisIconCache[color];
}
function getRadarIcon(color) {
    if (!radarIconCache[color]) {
        radarIconCache[color] = makeIcon(makeRadarSvg(color, 22), 22);
    }
    return radarIconCache[color];
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

    map = L.map(elementId, { zoomControl: false }).setView([lat, lon], zoom);
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

    osmBaseLayer = L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
        maxZoom: 19,
        attribution: '&copy; OpenStreetMap contributors',
        referrerPolicy: 'strict-origin-when-cross-origin'
    }).addTo(map);

    seaBaseLayer = L.tileLayer('https://tiles.openseamap.org/seamark/{z}/{x}/{y}.png', {
        maxZoom: 19,
        attribution: '&copy; OpenSeaMap',
        opacity: 0.8,
        referrerPolicy: 'strict-origin-when-cross-origin'
    }).addTo(map);

    // featureGroup (not layerGroup) so zoomToTrack can call getBounds() on it.
    trackLayer = L.featureGroup().addTo(map);
    boatMarker = L.marker([lat, lon], { icon: selfIcon, zIndexOffset: 1000 }).addTo(map);
    boatVector = L.polyline([], { color: '#f9a8d4', weight: 1.5, dashArray: '6,4', opacity: 0.8 }).addTo(map);

    // Map click: in route edit mode, add waypoint. Otherwise just dismiss menus.
    map.on('click', (e) => {
        if (routeEditMode) {
            addEditWaypoint(e.latlng.lat, e.latlng.lng);
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
        if (routeEditMode || !dotNetRef) return;
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

    // Delegated click handler: AIS popup buddy-toggle links tag themselves
    // with data-ona-buddy so we can route them to Blazor without leaking a
    // callback through each popup's HTML.
    mapEl.addEventListener('click', (e) => {
        const a = e.target && e.target.closest ? e.target.closest('a[data-ona-buddy]') : null;
        if (!a || !dotNetRef) return;
        e.preventDefault();
        e.stopPropagation();
        dotNetRef.invokeMethodAsync('OnToggleBuddy',
            a.getAttribute('data-ctx') || '',
            a.getAttribute('data-mmsi') || null,
            a.getAttribute('data-nm') || null,
            a.getAttribute('data-is') === '1');
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

        const cpaInfo = computeCpa(selfLat, selfLon, selfCogRad, selfSogMs,
                                    v.lat, v.lon, v.cogRad, v.sogMs);
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
        // Radar targets use their own outline-triangle icon in a fixed tan
        // tone; AIS targets fall back to the per-ship-type colour scale.
        const isRadar = v.source === 'radar';
        const color = isRadar
            ? (isDangerEff ? AIS_COLORS.danger : RADAR_COLOR)
            : aisColor(v.shipType, isDangerEff, v.buddy);
        const icon = isRadar ? getRadarIcon(color) : getAisIcon(color);

        let marker = aisMarkers[v.context];
        if (!marker) {
            marker = L.marker([v.lat, v.lon], { icon }).addTo(map);
            aisMarkers[v.context] = marker;
        } else {
            marker.setLatLng([v.lat, v.lon]);
            marker.setIcon(icon);
        }
        rotateMarker(marker, v.cogRad ?? v.headingRad);

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

        // Buddy toggle: inline data attributes let us hook a delegated click
        // below without escaping a callback through string concatenation.
        const buddyLabel = v.buddy ? '\u2605 Remove buddy' : '\u2606 Add buddy';
        const buddyAttrs = `data-ona-buddy="1" data-ctx="${esc(v.context)}" data-mmsi="${esc(mmsi || '')}"`
            + ` data-nm="${esc(v.name || '')}" data-is="${v.buddy ? '1' : '0'}"`;

        let linksHtml = '';
        if (mmsi) {
            linksHtml = `<div style="margin-top:6px;padding-top:6px;border-top:1px solid rgba(255,255,255,0.08);display:flex;gap:10px;flex-wrap:wrap">` +
                `<a href="${mtUrl}" target="_blank" rel="noopener" style="color:#7dd3fc;font-size:11px;text-decoration:none">MarineTraffic</a>` +
                `<a href="${vfUrl}" target="_blank" rel="noopener" style="color:#7dd3fc;font-size:11px;text-decoration:none">VesselFinder</a>` +
                `<a href="#" ${buddyAttrs} style="color:#facc15;font-size:11px;text-decoration:none">${buddyLabel}</a>` +
                `</div>`;
        }

        const popupHtml =
            `<div class="ais-popup-content">` +
            `<div class="ais-popup-title">${displayTitle}</div>` +
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
            const labelText = `${cpaInfo.cpa.toFixed(2)} nm · T-${cpaInfo.tcpa.toFixed(0)}m`;
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

export function clearGuardZone() {
    if (guardZoneRing) { map.removeLayer(guardZoneRing); guardZoneRing = null; }
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
export function addChartLayer(id, tileUrl, minZoom, maxZoom, opacity, bounds) {
    if (!map || chartLayers[id]) return false;
    const opts = {
        minZoom: minZoom || 1,
        maxZoom: maxZoom || 18,
        opacity: opacity || 0.8,
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
    chartLayers[id] = layer;
    return true;
}

export function removeChartLayer(id) {
    const layer = chartLayers[id];
    if (layer && map) {
        map.removeLayer(layer);
        delete chartLayers[id];
    }
}

export function setChartLayerOpacity(id, opacity) {
    const layer = chartLayers[id];
    if (layer) layer.setOpacity(opacity);
}

// --- Routes ---

// Route polyline colour. Warm amber contrasts cleanly with OSM blue
// water and doesn't clash with AIS ship colours (same family).
const ROUTE_COLOR = '#e09f3e';

// Add a route as a polyline. coords is [[lat, lon], ...].
export function addRoute(id, name, coords) {
    if (!map || routeLayers[id]) return;
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
    routeLayers[id] = group;
}

export function removeRoute(id) {
    const layer = routeLayers[id];
    if (layer && map) { map.removeLayer(layer); delete routeLayers[id]; }
}

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
    } else if (routeEditLine) {
        routeEditLine.setLatLngs(routeEditCoords);
    }
}

function addEditWaypoint(lat, lon) {
    const idx = routeEditCoords.length;
    routeEditCoords.push([lat, lon]);

    const marker = L.marker([lat, lon], {
        icon: makeEditWpIcon(idx + 1),
        draggable: true,
        zIndexOffset: 800
    }).addTo(routeEditLayer);

    marker.on('drag', (e) => {
        const ll = e.target.getLatLng();
        routeEditCoords[idx] = [ll.lat, ll.lng];
        redrawEditLine();
    });

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

// --- Waypoint Markers ---

const waypointMarkers = {};

// Waypoint marker colour. Terracotta is a step warmer/redder than the
// route amber so a bare waypoint reads distinct from a route dot.
const WAYPOINT_COLOR = '#c76f51';

export function addWaypointMarker(id, lat, lon, name) {
    if (!map || waypointMarkers[id]) return;
    const marker = L.circleMarker([lat, lon], {
        radius: 6, color: WAYPOINT_COLOR, fillColor: WAYPOINT_COLOR, fillOpacity: 1, weight: 2
    }).addTo(map);
    marker.bindTooltip(name || id.substring(0, 8), {
        permanent: false, direction: 'right', offset: [10, 0],
        className: 'bearing-tooltip'
    });
    waypointMarkers[id] = marker;
}

export function removeWaypointMarker(id) {
    if (waypointMarkers[id] && map) {
        map.removeLayer(waypointMarkers[id]);
        delete waypointMarkers[id];
    }
}

// --- Weather Overlay ---

let weatherLayer = null;

// Add OpenWeatherMap wind speed overlay. Requires a free API key from openweathermap.org.
// Tile URL: https://tile.openweathermap.org/map/{layer}/{z}/{x}/{y}.png?appid={key}
export function setWeatherOverlay(tileUrl) {
    clearWeatherOverlay();
    if (!map || !tileUrl) return;
    weatherLayer = L.tileLayer(tileUrl, {
        maxZoom: 15,
        opacity: 0.5,
        attribution: '&copy; OpenWeatherMap'
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
    for (const id of Object.keys(chartLayers)) delete chartLayers[id];
    for (const id of Object.keys(routeLayers)) delete routeLayers[id];
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
    for (const id of Object.keys(waypointMarkers)) delete waypointMarkers[id];
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
