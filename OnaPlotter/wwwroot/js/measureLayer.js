// Persistent measurement tool. Multi-segment ruler: clicks drop
// points, each segment is labelled with bearing + distance and the
// last point shows a running total. A point can be vessel-anchored
// (tracks own-boat as it moves) or fixed on the chart, which lets the
// helm answer both "how far is that island from where I am?" and
// "how long is this planned leg?" with the same tool.
//
// Editing model mirrors Route Edit so the helm doesn't have to learn
// a new gesture vocabulary:
//   * Drag a fixed point to move it (vessel-anchored points are
//     non-draggable since they track own-boat live).
//   * Tap a segment to insert a new point at the click location.
//   * Right-click / long-press anywhere clears the ruler without
//     leaving Measure mode.

import { haversineMeters, bearingDeg, NM_PER_METER } from './geoMath.js';
import { measureDistance } from './format.js';

let mapRef = null;
let colors = null;
let pointToSegmentPixels = null;   // Pixel-distance helper from leafletInterop.

let measureActive = false;
let measurePoints = [];          // [{ lat, lon, vessel: bool }, ...]
let measureMarkers = [];         // L.marker[]   parallel to measurePoints
let measureSegments = [];        // L.polyline[] one per segment between points
let measureHitLines = [];        // L.polyline[] thick invisible per-segment hitbox
let measureTooltips = [];        // L.tooltip[]  one per segment, anchored at end
let measureSuppressNextMapClick = false;

// Latest own-boat position. Pushed from the mux on every updatePosition
// tick so vessel-anchored measurement segments redraw against fresh
// boat coords instead of stale ones.
let selfLat = 0, selfLon = 0;

export function init(map, deps) {
    mapRef = map;
    colors = deps.colors;
    pointToSegmentPixels = deps.pointToSegmentPixels;
}

// Public predicates used by the mux's map-click handler.
export function isActive() { return measureActive; }
export function consumeSuppressNextMapClick() {
    if (!measureSuppressNextMapClick) return false;
    measureSuppressNextMapClick = false;
    return true;
}

// Mux pushes the latest own-boat position. While a vessel-anchored
// measurement segment is on the chart, every tick we redraw to keep
// its bearing/distance label honest as the boat moves.
export function setBoatPosition(lat, lon) {
    selfLat = lat;
    selfLon = lon;
    if (measureActive && measurePoints.length > 0 && hasVesselMeasurePoint()) {
        // Geometry-only incremental update instead of a full
        // redrawMeasure teardown. Fires every position fix (~1 Hz),
        // so the previous full rebuild was N marker create/destroy +
        // 2N polyline create/destroy + N tooltip create/destroy +
        // N marker event-listener re-bind per second on a vessel-
        // anchored ruler. The geometry-only path moves each vessel
        // marker via setLatLng and refreshes segment geometry + the
        // running-total tooltips without recreating any DOM.
        redrawMeasureLive();
    }
}

export function setMeasureMode(active) {
    measureActive = !!active;
    if (!measureActive) clearMeasure();
    if (mapRef) {
        // Visual hint: crosshair cursor when in measurement mode.
        mapRef.getContainer().style.cursor = measureActive ? 'crosshair' : '';
    }
}

function removeMeasureLayers() {
    if (!mapRef) {
        measureMarkers = []; measureSegments = []; measureHitLines = []; measureTooltips = [];
        return;
    }
    for (const m of measureMarkers) mapRef.removeLayer(m);
    for (const s of measureSegments) mapRef.removeLayer(s);
    for (const h of measureHitLines) mapRef.removeLayer(h);
    for (const t of measureTooltips) mapRef.removeLayer(t);
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

export function addMeasurePoint(lat, lon) {
    if (!mapRef) return;
    measurePoints.push({ lat, lon, vessel: false });
    redrawMeasure();
}

// Adds a vessel-anchored measurement point. Used by the boat-marker
// click handler in measure mode and by measureFromVesselTo() below.
export function addVesselMeasurePoint() {
    if (!mapRef) return;
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
        }).addTo(mapRef);
        ghostLine = L.polyline([origLL, origLL], {
            color: colors.measure, weight: 1.5, opacity: 0.7, dashArray: '3,4',
            interactive: false
        }).addTo(mapRef);
        // direction:'top' + a > marker-half-height offset keeps the
        // Δ label above the cursor/finger so the helm can read it
        // mid-drag instead of staring at a marker that hides its own
        // delta. -14 clears the 14 px measure dot (7 px to top edge
        // + a little air).
        ghostLine.bindTooltip('Δ 0 m', {
            permanent: true,
            direction: 'top',
            offset: [0, -14],
            className: 'measure-tooltip'
        }).openTooltip(origLL);
    });

    marker.on('drag', (e) => {
        const ll = e.target.getLatLng();
        if (idx < 0 || idx >= measurePoints.length) return;
        measurePoints[idx].lat = ll.lat;
        measurePoints[idx].lon = ll.lng;
        // Live update of just the segments adjacent to this marker -
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
        if (ghostMarker && mapRef) mapRef.removeLayer(ghostMarker);
        if (ghostLine && mapRef) mapRef.removeLayer(ghostLine);
        ghostMarker = null; ghostLine = null; origLL = null;
        // Final canonical redraw so running totals on every tooltip
        // reflect the new geometry.
        redrawMeasure();
    });
}

// Boat-position-driven incremental redraw. Updates every vessel-
// anchored marker's position to the new own-boat coords, then
// refreshes every segment + running-total tooltip downstream. Used
// by setBoatPosition so a vessel-anchored ruler stays live as the
// boat moves without the per-tick teardown of redrawMeasure.
function redrawMeasureLive() {
    if (!mapRef || measurePoints.length < 2) return;
    for (let i = 0; i < measurePoints.length; i++) {
        const p = measurePoints[i];
        const marker = measureMarkers[i];
        if (p.vessel && marker) {
            marker.setLatLng([selfLat, selfLon]);
        }
    }
    updateMeasureSegmentsAround(-1);
}

// Update the geometry + tooltips of segments that touch point `idx`,
// plus refresh every later tooltip's running total. Used during drag
// where a full tear-down would interrupt Leaflet's drag tracking.
function updateMeasureSegmentsAround(_idx) {
    if (!mapRef || measurePoints.length < 2) return;
    const positions = measurePoints.map(measurePointLatLng);

    // Recompute the running total once and walk segments updating
    // both the visible polyline geometry and each tooltip's content
    // (only the affected ones strictly need geometry, but content
    // depends on the running total which shifts when any earlier
    // segment changed length).
    let runningM = 0;
    for (let i = 1; i < positions.length; i++) {
        const a = positions[i - 1];
        const b = positions[i];
        const segM = haversineMeters(a[0], a[1], b[0], b[1]);
        const segBrg = bearingDeg(a[0], a[1], b[0], b[1]);
        runningM += segM;

        const segLayer = measureSegments[i - 1];
        const hitLayer = measureHitLines[i - 1];
        const tipLayer = measureTooltips[i - 1];
        if (segLayer) segLayer.setLatLngs([a, b]);
        if (hitLayer) hitLayer.setLatLngs([a, b]);
        if (tipLayer) {
            tipLayer.setLatLng(b);
            tipLayer.setContent(`${segBrg.toFixed(0)}&deg; / ${measureDistance(segM)}<br/>total ${measureDistance(runningM)}`);
        }
    }
}

// Find the segment closest to `ll` and splice a new fixed point in at
// that position. Mirrors insertEditVertexOnSegment for routes; the
// suppress flag stops the trailing map-click from appending a phantom
// duplicate point at the end of the ruler.
function insertMeasurePointOnSegment(ll) {
    if (!mapRef || measurePoints.length < 2) return;
    const positions = measurePoints.map(measurePointLatLng);
    const p = mapRef.latLngToLayerPoint(ll);
    let bestIdx = 0;
    let bestDist = Infinity;
    for (let i = 0; i < positions.length - 1; i++) {
        const a = mapRef.latLngToLayerPoint(L.latLng(positions[i][0], positions[i][1]));
        const b = mapRef.latLngToLayerPoint(L.latLng(positions[i + 1][0], positions[i + 1][1]));
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
    if (!mapRef) return;
    removeMeasureLayers();
    if (measurePoints.length === 0) return;

    const positions = measurePoints.map(measurePointLatLng);

    // Pre-compute running totals so the last-point tooltip can show
    // the cumulative distance without re-walking the array each tick.
    let runningTotalM = 0;
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
        }).addTo(mapRef);
        if (!point.vessel) bindMeasureMarker(marker, i);
        measureMarkers.push(marker);

        if (i >= 1) {
            const a = positions[i - 1];
            const b = positions[i];
            const segM = haversineMeters(a[0], a[1], b[0], b[1]);
            const segBrg = bearingDeg(a[0], a[1], b[0], b[1]);
            runningTotalM += segM;

            // Visible dashed segment.
            const seg = L.polyline([a, b], {
                color: colors.measure, weight: 2, dashArray: '6,4', opacity: 0.85,
                className: 'ona-measure-line',
            }).addTo(mapRef);
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
                color: colors.measure, weight: 24, opacity: 0,
                interactive: true, className: 'ona-measure-hit-line',
            }).addTo(mapRef);
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
                .setContent(`${segBrg.toFixed(0)}&deg; / ${measureDistance(segM)}<br/>total ${measureDistance(runningTotalM)}`)
                .addTo(mapRef);
            measureTooltips.push(tooltip);
        }
    }
}

// Public entry point for "Measure to here" in the map context menu.
// Drops a fresh two-point measurement: vessel as the moving anchor,
// the clicked spot as the fixed endpoint. Activates measure mode so
// the helm can keep tapping to extend the ruler if they want a
// multi-leg distance.
export function measureFromVesselTo(lat, lon) {
    if (!mapRef) return;
    clearMeasure();
    measureActive = true;
    mapRef.getContainer().style.cursor = 'crosshair';
    addVesselMeasurePoint();
    addMeasurePoint(lat, lon);
}

// Public entry point for "Measure from here" in the map context menu.
// Seeds a one-point measurement at the clicked spot and activates
// measure mode so the next tap on the chart extends the ruler. The
// counterpart to measureFromVesselTo: that one anchors to the boat
// and ends at the click; this one starts at the click and leaves
// the endpoint to the user's next tap.
export function measureFromPoint(lat, lon) {
    if (!mapRef) return;
    clearMeasure();
    measureActive = true;
    mapRef.getContainer().style.cursor = 'crosshair';
    addMeasurePoint(lat, lon);
}

export function dispose() {
    measureActive = false;
    measurePoints = [];
    measureMarkers = [];
    measureSegments = [];
    measureHitLines = [];
    measureTooltips = [];
    measureSuppressNextMapClick = false;
    selfLat = 0; selfLon = 0;
    mapRef = null;
    colors = null;
    pointToSegmentPixels = null;
}
