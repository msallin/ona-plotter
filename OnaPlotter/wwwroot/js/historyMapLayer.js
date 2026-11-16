/**
 * Leaflet renderer for the History page's own map instance.
 *
 * Separate from leafletInterop.js because the History map is a second,
 * independent L.map with its own lifecycle: created when the page mounts,
 * removed when it unmounts. C# (HistoryMapJs + HistorySegmentRender)
 * decides every colour, style and tooltip; this module only turns the
 * shaped payloads into Leaflet layers and reports pans / clicks back
 * through the DotNet reference handed to initHistoryMap.
 *
 * Colours arrive either as plain CSS colours ("#a78bfa") or as theme
 * token references with a fallback ("var(--map-own, #ec4899)"); the
 * latter resolve against the live :root custom properties so the track
 * follows a theme flip on the next render.
 */

let map = null;
let dotNetRef = null;
let boatMarker = null;
let playbackMarker = null;
/** Segment layers keyed by their index into C#'s segments array. */
const segmentLayers = new Map();
/** Chart tile layers keyed by chart identifier. */
const chartLayers = new Map();

// Matches "var(--map-own, #ec4899)" -> name "--map-own", fallback "#ec4899".
// Matches "var(--map-own)"          -> name "--map-own", fallback undefined.
const CSS_VAR_PATTERN = /^var\(\s*(--[\w-]+)\s*(?:,\s*(.+?))?\s*\)$/;

/**
 * Resolve a colour spec to a concrete colour string. Pure: the custom
 * property lookup is injected so node:test can exercise it.
 * @param {string} spec plain colour or "var(--token, fallback)".
 * @param {(name: string) => string} readVar returns the token's value or "".
 * @returns {string}
 */
export function resolveColor(spec, readVar) {
    if (typeof spec !== 'string') return '';
    const m = CSS_VAR_PATTERN.exec(spec.trim());
    if (!m) return spec;
    const value = (readVar(m[1]) || '').trim();
    return value || (m[2] ?? '').trim();
}

/**
 * Index of the coordinate nearest to (lat, lng) by squared degree
 * distance. Good enough for a hover readout over a few thousand points;
 * returns -1 for an empty list.
 * @param {number[][]} coords [[lat, lon], ...]
 */
export function nearestIndex(coords, lat, lng) {
    let best = -1;
    let bestDist = Infinity;
    for (let i = 0; i < coords.length; i++) {
        const dLat = coords[i][0] - lat;
        const dLon = coords[i][1] - lng;
        const d = dLat * dLat + dLon * dLon;
        if (d < bestDist) { bestDist = d; best = i; }
    }
    return best;
}

/**
 * Tooltip body for a hover at coordinate index idx: the static tooltip
 * plus a "Depth here" line when the segment carries a depth sample there.
 */
export function depthTooltip(tooltip, depths, idx) {
    const depth = depths && idx >= 0 ? depths[idx] : null;
    if (depth === null || depth === undefined || !isFinite(depth)) return tooltip;
    return `${tooltip}<br>Depth here: ${depth.toFixed(1)} m`;
}

function cssColor(spec) {
    const style = getComputedStyle(document.documentElement);
    return resolveColor(spec, (name) => style.getPropertyValue(name));
}

/**
 * Wraps fn so bursts of calls run it at most once per animation frame,
 * with the latest arguments. `cancel()` drops a pending call.
 */
export function frameThrottle(fn, schedule = (cb) => requestAnimationFrame(cb),
                              unschedule = (id) => cancelAnimationFrame(id)) {
    let pending = null;
    let latest = null;
    const throttled = (...args) => {
        latest = args;
        if (pending !== null) return;
        pending = schedule(() => {
            pending = null;
            fn(...latest);
        });
    };
    throttled.cancel = () => {
        if (pending !== null) unschedule(pending);
        pending = null;
    };
    return throttled;
}

function invokeDotNet(method, ...args) {
    if (!dotNetRef) return;
    // A click / pan can land after the page disposed its reference.
    dotNetRef.invokeMethodAsync(method, ...args).catch(() => { });
}

/**
 * Create the map in the element with the given id. Any previous History
 * map is removed first so a fast unmount / remount never leaks one.
 * @param {{url: string, maxZoom: number, opacity: number, attribution: string}[]} baseLayers
 */
export function initHistoryMap(elementId, lat, lon, zoom, baseLayers, ref) {
    disposeHistoryMap();
    dotNetRef = ref;
    map = L.map(elementId, { zoomControl: true }).setView([lat, lon], zoom);
    for (const b of baseLayers || []) {
        L.tileLayer(b.url, {
            maxZoom: b.maxZoom,
            opacity: b.opacity,
            attribution: b.attribution,
            referrerPolicy: 'strict-origin-when-cross-origin',
        }).addTo(map);
    }
    // moveend also fires after every zoom. The bounds ride along so C#
    // doesn't need a second interop call to read them back.
    map.on('moveend', () => {
        const b = map.getBounds();
        invokeDotNet('OnHistoryViewportChanged', b.getSouth(), b.getWest(), b.getNorth(), b.getEast());
    });
}

export function disposeHistoryMap() {
    if (map) map.remove();
    map = null;
    dotNetRef = null;
    boatMarker = null;
    playbackMarker = null;
    segmentLayers.clear();
    chartLayers.clear();
}

export function setView(lat, lon, zoom) {
    if (map) map.setView([lat, lon], zoom);
}

/** @returns {number[] | null} [south, west, north, east] */
export function getViewportBounds() {
    if (!map) return null;
    const b = map.getBounds();
    return [b.getSouth(), b.getWest(), b.getNorth(), b.getEast()];
}

export function setBoatMarker(lat, lon, color) {
    if (!map) return;
    if (boatMarker) map.removeLayer(boatMarker);
    const col = cssColor(color);
    boatMarker = L.circleMarker([lat, lon], {
        radius: 7, color: col, fillColor: col, fillOpacity: 1, weight: 2,
    }).bindTooltip('Current position').addTo(map);
}

/** @param {number[] | null} bounds Leaflet [[south, west], [north, east]] or null. */
export function addChartLayer(id, url, minZoom, maxZoom, opacity, bounds) {
    if (!map || chartLayers.has(id)) return;
    const opts = {
        minZoom, maxZoom, opacity,
        keepBuffer: 6,
        crossOrigin: 'anonymous',
        attribution: '',
        errorTileUrl: '',
    };
    if (bounds) opts.bounds = L.latLngBounds(bounds[0], bounds[1]);
    const layer = L.tileLayer(url, opts);
    layer.setZIndex(50);
    layer.addTo(map);
    chartLayers.set(id, layer);
}

export function removeChartLayer(id) {
    const layer = chartLayers.get(id);
    if (!map || !layer) return;
    map.removeLayer(layer);
    chartLayers.delete(id);
}

const STYLE_OPTIONS = {
    moving: { weight: 3.5, opacity: 0.9, lineJoin: 'round', lineCap: 'round' },
    stationaryLine: { weight: 2.5, opacity: 0.55, dashArray: '4 4', lineJoin: 'round', lineCap: 'round' },
};

function buildSegmentLayer(s) {
    const color = cssColor(s.color);
    const tooltipOpts = { sticky: true, direction: 'top' };
    if (s.style === 'stationaryDot') {
        const mid = s.coords[Math.floor(s.coords.length / 2)];
        return L.circleMarker(mid, {
            radius: 5, color, fillColor: color, fillOpacity: 0.6, weight: 1,
        }).bindTooltip(s.tooltip, tooltipOpts);
    }
    const layer = L.polyline(s.coords, { ...STYLE_OPTIONS[s.style], color })
        .bindTooltip(s.tooltip, tooltipOpts);
    if (s.depths) {
        // nearestIndex walks every coordinate of the segment; mousemove
        // fires faster than the screen refreshes. Once per frame, and the
        // tooltip is only rewritten when the nearest point changes.
        let shownIdx = -1;
        const hover = frameThrottle((latlng) => {
            const idx = nearestIndex(s.coords, latlng.lat, latlng.lng);
            if (idx === shownIdx) return;
            shownIdx = idx;
            layer.setTooltipContent(depthTooltip(s.tooltip, s.depths, idx));
        });
        layer.on('mousemove', (ev) => hover(ev.latlng));
        // Restore the static tooltip so a re-hover doesn't show the
        // previous hover's depth until the pointer moves.
        layer.on('mouseout', () => {
            hover.cancel();
            shownIdx = -1;
            layer.setTooltipContent(s.tooltip);
        });
    }
    return layer;
}

/**
 * Replace every segment layer with the given payload.
 * @param {{index: number, coords: number[][], style: string, color: string,
 *          tooltip: string, depths: (number|null)[] | null}[]} segments
 */
export function renderSegments(segments, fitBounds) {
    if (!map) return;
    for (const layer of segmentLayers.values()) map.removeLayer(layer);
    segmentLayers.clear();
    let bounds = null;
    for (const s of segments || []) {
        if (!s.coords || s.coords.length === 0) continue;
        const layer = buildSegmentLayer(s).addTo(map);
        layer.on('click', (ev) => {
            // The map's own click handler dismisses menus; keep it out.
            L.DomEvent.stopPropagation(ev);
            invokeDotNet('OpenTripDetailFromMap', s.index);
        });
        segmentLayers.set(s.index, layer);
        const b = layer.getBounds ? layer.getBounds() : L.latLngBounds(layer.getLatLng(), layer.getLatLng());
        bounds = bounds ? bounds.extend(b) : b;
    }
    if (fitBounds && bounds) map.fitBounds(bounds, { padding: [40, 40] });
}

/** Fit the viewport to one segment, keyed by its index into C#'s segments array. */
export function fitSegment(index) {
    const layer = segmentLayers.get(index);
    if (!map || !layer) return;
    map.invalidateSize();
    const b = layer.getBounds ? layer.getBounds() : L.latLngBounds(layer.getLatLng(), layer.getLatLng());
    map.fitBounds(b, { padding: [40, 40] });
}

export function setPlaybackMarker(lat, lon, color) {
    if (!map) return;
    if (playbackMarker) map.removeLayer(playbackMarker);
    const col = cssColor(color);
    playbackMarker = L.circleMarker([lat, lon], {
        radius: 8, color: col, fillColor: col, fillOpacity: 1, weight: 2,
    }).bindTooltip('Playback').addTo(map);
}

export function movePlaybackMarker(lat, lon) {
    if (playbackMarker) playbackMarker.setLatLng([lat, lon]);
}
