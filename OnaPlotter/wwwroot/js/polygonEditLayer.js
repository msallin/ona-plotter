// Polygon-edit overlay: closed polygon (>= 3 vertices) or 2-vertex
// preview line, plus numbered draggable vertex markers with the
// route-edit ghost + delta tooltip on drag.
//
// Parallel to routeEditLayer but the saved shape is a closed polygon,
// not an open polyline. When the user saves, Map.razor pulls the
// coords and POSTs them through RegionApi.CreatePolygonAsync.

import { attachDragGhost } from './dragGhost.js';

// Polygon edit uses the same violet as route-edit so "I am editing"
// reads consistently across both drawing modes. Amber (--ann-region)
// would match finished regions but fight the route-edit cue, so the
// in-edit colour stays the same regardless of shape; saved-region
// amber kicks in on save.
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
    attachDragGhost(marker, {
        container: () => polygonEditLayer,
        color: () => POLYGON_COLOR,
        icon: { className: 'edit-wp-ghost-icon', html: '<div class="edit-wp-ghost-circle"></div>', size: 24 },
        // Clears the 24 px draggable vertex icon (12 px to its top edge + air).
        tooltipOffset: -18,
        onDrag: (ll) => {
            polygonEditCoords[idx] = [ll.lat, ll.lng];
            scheduleRedrawPolygonShape();
        }
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
