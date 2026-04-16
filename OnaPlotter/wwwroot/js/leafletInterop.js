// Leaflet JS interop for the chartplotter map.
// All map state lives here; Blazor calls exported functions via IJSRuntime.

import { RAD, DEG, NM_PER_METER, VECTOR_MINUTES, SPEED_BUCKETS,
         haversineMeters, bearingDeg, destPoint, vectorEnd,
         computeCpa, speedColor, speedBucket } from './geoMath.js';

let map = null;
let boatMarker = null;
let boatVector = null;
let trackLayer = null;
let followBoat = true;
let northUp = true;
let nightMode = false;
let dotNetRef = null;
let suppressMoveEnd = false;  // Suppress moveend during programmatic panTo.

// AIS state.
const aisMarkers = {};
const aisVectors = {};

// Chart layers from SignalK.
const chartLayers = {};  // keyed by chart identifier
let osmBaseLayer = null;
let seaBaseLayer = null;

// Routes and server track.
const routeLayers = {};  // keyed by route ID
let serverTrackLayer = null;

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

// Own vessel state cache (for CPA calculations).
let selfLat = 0, selfLon = 0, selfCogRad = null, selfSogMs = null;

// HTML-escape untrusted strings for popup content.
function esc(s) { const d = document.createElement('div'); d.textContent = s; return d.innerHTML; }

// --- Icons ---

function makeBoatSvg(fill, size) {
    const s = size || 28;
    const h = s / 2;
    return `<svg width="${s}" height="${s}" viewBox="-${h} -${h} ${s} ${s}">
              <polygon points="0,-${h-3} ${h-7},${h-5} 0,${h-9} -${h-7},${h-5}" fill="${fill}" stroke="#fff" stroke-width="1.5"/>
            </svg>`;
}

function makeIcon(html, size) {
    return L.divIcon({ className: 'boat-icon', html, iconSize: [size, size], iconAnchor: [size/2, size/2] });
}

const selfIcon = makeIcon(makeBoatSvg('#3b82f6', 28), 28);

// AIS colors by vessel type category.
const AIS_COLORS = {
    cargo: '#4ade80',     // green
    tanker: '#f97316',    // orange
    passenger: '#a78bfa', // purple
    fishing: '#facc15',   // yellow
    sailing: '#38bdf8',   // light blue
    pleasure: '#38bdf8',
    tug: '#fb923c',       // orange-light
    military: '#ef4444',  // red
    sar: '#ef4444',
    default: '#f59e0b',   // amber
    danger: '#ef4444'     // red for CPA danger
};

function aisColor(shipType, isDanger) {
    if (isDanger) return AIS_COLORS.danger;
    if (!shipType) return AIS_COLORS.default;
    const t = shipType.toLowerCase();
    for (const [key, color] of Object.entries(AIS_COLORS)) {
        if (t.includes(key)) return color;
    }
    return AIS_COLORS.default;
}

// AIS icon cache to avoid creating new icons for every update.
const aisIconCache = {};
function getAisIcon(color) {
    if (!aisIconCache[color]) {
        aisIconCache[color] = makeIcon(makeBoatSvg(color, 22), 22);
    }
    return aisIconCache[color];
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

    map = L.map(elementId, { zoomControl: false }).setView([lat, lon], zoom);

    // Zoom control in top-right to avoid HUD overlap.
    L.control.zoom({ position: 'topright' }).addTo(map);

    osmBaseLayer = L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
        maxZoom: 19,
        attribution: '&copy; OSM'
    }).addTo(map);

    seaBaseLayer = L.tileLayer('https://tiles.openseamap.org/seamark/{z}/{x}/{y}.png', {
        maxZoom: 19,
        attribution: '&copy; OpenSeaMap',
        opacity: 0.8
    }).addTo(map);

    trackLayer = L.layerGroup().addTo(map);
    boatMarker = L.marker([lat, lon], { icon: selfIcon, zIndexOffset: 1000 }).addTo(map);
    boatVector = L.polyline([], { color: '#3b82f6', weight: 2, dashArray: '8,5' }).addTo(map);

    // Map click -> bearing/distance line.
    map.on('click', (e) => {
        if (!dotNetRef) return;
        drawBearingLine(e.latlng.lat, e.latlng.lng);
    });
    map.on('dblclick', () => clearBearingLine());

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

    const end = vectorEnd(lat, lon, cogRad, sogMs);
    if (end) boatVector.setLatLngs([[lat, lon], end]);
    else boatVector.setLatLngs([]);

    if (followBoat) {
        suppressMoveEnd = true;
        map.panTo([lat, lon], { animate: true, duration: 0.5 });
        // Re-enable after the pan animation completes.
        setTimeout(() => { suppressMoveEnd = false; }, 600);
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

        const cpaInfo = computeCpa(selfLat, selfLon, selfCogRad, selfSogMs,
                                    v.lat, v.lon, v.cogRad, v.sogMs);
        const isDanger = cpaInfo && cpaInfo.cpa < 0.5 && cpaInfo.tcpa < 30 && cpaInfo.tcpa > 0;
        const color = aisColor(v.shipType, isDanger);
        const icon = getAisIcon(color);

        let marker = aisMarkers[v.context];
        if (!marker) {
            marker = L.marker([v.lat, v.lon], { icon }).addTo(map);
            aisMarkers[v.context] = marker;
        } else {
            marker.setLatLng([v.lat, v.lon]);
            marker.setIcon(icon);
        }
        rotateMarker(marker, v.cogRad ?? v.headingRad);

        // Name label visible at zoom >= 12.
        const displayName = v.name || (v.mmsi ? v.mmsi : null);
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

        // Popup with full info.
        const name = esc(v.name || 'Unknown');
        const mmsi = esc(v.mmsi || '---');
        const sog = v.sogMs != null ? (v.sogMs * 1.94384).toFixed(1) + ' kn' : '---';
        const cog = v.cogRad != null ? (v.cogRad * DEG).toFixed(0) + '&deg;' : '---';
        const type = esc(v.shipType || '');
        const dist = haversineMeters(selfLat, selfLon, v.lat, v.lon) * NM_PER_METER;

        let cpaText = '';
        if (cpaInfo && cpaInfo.tcpa > 0) {
            const cpaCls = isDanger ? 'color:#ef4444;font-weight:bold' : '';
            cpaText = `<div style="${cpaCls};margin-top:4px;padding-top:4px;border-top:1px solid rgba(255,255,255,0.1)">` +
                       `CPA ${cpaInfo.cpa.toFixed(2)} nm in ${cpaInfo.tcpa.toFixed(0)} min</div>`;
        }

        marker.bindPopup(
            `<div style="line-height:1.6">` +
            `<b style="font-size:13px">${name}</b>` +
            (type ? ` <span style="opacity:0.5;font-size:11px">${type}</span>` : '') +
            `<br><span style="opacity:0.5">MMSI ${mmsi}</span>` +
            `<div style="display:flex;gap:12px;margin-top:2px">` +
              `<span>SOG ${sog}</span><span>COG ${cog}</span>` +
            `</div>` +
            `<div style="opacity:0.7">${dist.toFixed(2)} nm away</div>` +
            cpaText +
            `</div>`,
            { closeButton: false, maxWidth: 240, className: 'ais-popup' }
        );

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
    }

    // Remove stale markers.
    for (const ctx of Object.keys(aisMarkers)) {
        if (!seen.has(ctx)) {
            map.removeLayer(aisMarkers[ctx]);
            delete aisMarkers[ctx];
            if (aisVectors[ctx]) { map.removeLayer(aisVectors[ctx]); delete aisVectors[ctx]; }
            delete aisLabels[ctx];
        }
    }
}

// --- Bearing/Distance ---

function drawBearingLine(lat, lon) {
    if (!map || !boatMarker) return;
    clearBearingLine();

    const dist = haversineMeters(selfLat, selfLon, lat, lon) * NM_PER_METER;
    const brg = bearingDeg(selfLat, selfLon, lat, lon);

    bearingLine = L.polyline([[selfLat, selfLon], [lat, lon]], {
        color: '#a78bfa', weight: 2, dashArray: '6,6', opacity: 0.8
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
}

export function clearAnchor() {
    if (anchorMarker) { map.removeLayer(anchorMarker); anchorMarker = null; }
    if (anchorCircle) { map.removeLayer(anchorCircle); anchorCircle = null; }
}

export function updateAnchorRadius(radiusM) {
    if (anchorCircle) anchorCircle.setRadius(radiusM);
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

// Add a route as a polyline. coords is [[lat, lon], ...].
export function addRoute(id, name, coords) {
    if (!map || routeLayers[id]) return;
    const line = L.polyline(coords, {
        color: '#a78bfa', weight: 2.5, opacity: 0.8, dashArray: '8,6'
    }).addTo(map);
    // Waypoint dots at each coordinate.
    const group = L.layerGroup([line]).addTo(map);
    for (let i = 0; i < coords.length; i++) {
        const dot = L.circleMarker(coords[i], {
            radius: 4, color: '#a78bfa', fillColor: '#a78bfa', fillOpacity: 1, weight: 1
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

// --- Keyboard shortcuts ---

let keyHandler = null;

export function enableKeyboardShortcuts(dotNetObjRef) {
    disableKeyboardShortcuts();
    keyHandler = (e) => {
        // Skip if user is typing in an input.
        if (e.target.tagName === 'INPUT' || e.target.tagName === 'TEXTAREA') return;
        const key = e.key.toLowerCase();
        if ('mfnat'.includes(key) && key.length === 1) {
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

export function zoomToTrack() {
    if (!trackLayer || !map) return;
    const bounds = trackLayer.getBounds();
    if (bounds.isValid()) map.fitBounds(bounds, { padding: [40, 40], maxZoom: 16 });
}

export function dispose() {
    if (map) { map.remove(); map = null; }
    boatMarker = null; boatVector = null; trackLayer = null;
    osmBaseLayer = null; seaBaseLayer = null; serverTrackLayer = null;
    for (const id of Object.keys(chartLayers)) delete chartLayers[id];
    for (const id of Object.keys(routeLayers)) delete routeLayers[id];
    for (const id of Object.keys(aisLabels)) delete aisLabels[id];
    bearingLine = null; bearingLabel = null;
    mobMarker = null; mobCircle = null; mobLine = null; mobLabel = null;
    anchorMarker = null; anchorCircle = null;
    for (const ctx of Object.keys(aisMarkers)) delete aisMarkers[ctx];
    for (const ctx of Object.keys(aisVectors)) delete aisVectors[ctx];
    dotNetRef = null;
}
