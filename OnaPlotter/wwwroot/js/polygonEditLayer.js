// Polygon-edit overlay: closed polygon (>= 3 vertices) or 2-vertex
// preview line, plus numbered draggable vertex markers with the
// route-edit ghost + delta tooltip on drag.
//
// Parallel to routeEditLayer but the saved shape is a closed polygon,
// not an open polyline. When the user saves, Map.razor pulls the
// coords and POSTs them through RegionApi.CreatePolygonAsync.

import { haversineMeters, NM_PER_METER } from './geoMath.js';

// Polygon edit uses the same violet as route-edit so "I am editing"
// reads consistently across both drawing modes. Was amber
// (--ann-region) which matched finished regions but fought the
// route-edit cue. User feedback: keep the in-edit colour the same
// regardless of shape; saved-region amber kicks in on save.
const POLYGON_COLOR = '#a78bfa';                    // --map-current

let mapRef = null;

let polygonEditMode = false;
let polygonEditLayer = null;
let polygonEditCoords = [];
let polygonEditMarkers = [];
let polygonEditShape = null;   // L.polygon once there are >= 3 vertices
let polygonEditLine = null;    // L.polyline for 2-vertex preview

export function init(map) {
    mapRef = map;
}

export function isActive() { return polygonEditMode; }

function makePolygonVertexIcon(num) {
    return L.divIcon({
        className: 'edit-wp-icon',
        html: `<div class="edit-wp-circle edit-poly-circle">${num}</div>`,
        iconSize: [24, 24],
        iconAnchor: [12, 12]
    });
}

// rAF-coalesced redraw used by the drag handler. Mirrors the pattern
// in routeEditLayer.scheduleRedrawEditLine - drag fires at ~60 Hz
// during a vertex move and each redrawPolygonShape re-projects every
// vertex of the polygon. The rAF gate collapses multiple drag events
// per frame into one redraw. Non-drag callers stay direct.
let _redrawPolygonShapeScheduled = false;
function scheduleRedrawPolygonShape() {
    if (_redrawPolygonShapeScheduled) return;
    _redrawPolygonShapeScheduled = true;
    requestAnimationFrame(() => {
        _redrawPolygonShapeScheduled = false;
        redrawPolygonShape();
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

// Reuses the route-edit ghost-marker + Delta tooltip pattern. The
// closure over idx captures the current index; removals tear down all
// markers and rebuild, so idx stays in sync with the coords array.
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
        // direction:'top' + a > marker-half-height offset keeps the
        // Δ label above the cursor/finger so the helm can read it
        // mid-drag instead of staring at a marker that hides its own
        // delta. -18 clears the 24 px draggable vertex icon (12 px
        // to top edge + a little air).
        ghostLine.bindTooltip('Δ 0 m', {
            permanent: true,
            direction: 'top',
            offset: [0, -18],
            className: 'measure-tooltip'
        }).openTooltip(origLL);
    });

    marker.on('drag', (e) => {
        const ll = e.target.getLatLng();
        polygonEditCoords[idx] = [ll.lat, ll.lng];
        scheduleRedrawPolygonShape();
        if (ghostLine && origLL) {
            ghostLine.setLatLngs([origLL, ll]);
            const dm = haversineMeters(origLL.lat, origLL.lng, ll.lat, ll.lng);
            const label = dm < 1000 ? `Δ ${dm.toFixed(0)} m` : `Δ ${(dm * NM_PER_METER).toFixed(2)} nm`;
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

export function addPolygonVertexInternal(lat, lon) {
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
    polygonEditLayer = L.layerGroup().addTo(mapRef);
}

// Seed an existing polygon's vertices into edit mode. Called from the
// Layers-panel Edit button on a region row. Mirrors loadRouteForEdit.
export function loadPolygonForEdit(coords) {
    stopPolygonEdit();
    polygonEditMode = true;
    polygonEditLayer = L.layerGroup().addTo(mapRef);
    if (!coords) return;
    for (const c of coords) {
        addPolygonVertexInternal(c[0], c[1]);
    }
}

export function stopPolygonEdit() {
    polygonEditMode = false;
    if (polygonEditLayer && mapRef) mapRef.removeLayer(polygonEditLayer);
    polygonEditLayer = null;
    polygonEditCoords = [];
    polygonEditMarkers = [];
    polygonEditShape = null;
    polygonEditLine = null;
}

export function getPolygonEditCoords() { return polygonEditCoords; }

// Vertex count + polygon area are derived in C# (see
// OnaPlotter.Utilities.PolygonGeometry) from the coords returned above
// so the formula stays unit-tested without a browser.

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
    // Rebuild markers without re-entering addPolygonVertexInternal
    // (which would redraw the polygon per vertex - O(n^2)). Build
    // the markers in one pass, then call redrawPolygonShape() once
    // at the end.
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

export function dispose() {
    polygonEditMode = false;
    polygonEditLayer = null;
    polygonEditCoords = [];
    polygonEditMarkers = [];
    polygonEditShape = null;
    polygonEditLine = null;
    mapRef = null;
}
