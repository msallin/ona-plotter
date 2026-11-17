// Route-edit overlay: dashed polyline + numbered draggable waypoint
// markers + a wide invisible hit polyline for finger-tap insertion of
// a vertex on an existing leg. Drag of a waypoint shows a "ghost"
// marker at the original position and a delta tooltip so the helm
// always sees "how far from where it was".

import { haversineMeters, NM_PER_METER } from './geoMath.js';
import { attachDragGhost } from './dragGhost.js';

let mapRef = null;
let colors = null;
let pointToSegmentPixels = null;

let routeEditMode = false;
let routeEditLayer = null;
let routeEditCoords = [];
let routeEditMarkers = [];
let routeEditLine = null;
let routeEditHitLine = null;
// One tooltip per leg, anchored at the segment midpoint, showing the
// leg's distance. Rebuilt on every redrawEditLine call so drag /
// insert / remove all keep the labels in sync. Distance only (no
// bearing) - bearing changes leg-by-leg as the boat sails the route
// and the active-route layer renders the live numbers; the static
// edit-time label is for "is this leg too long?" planning.
let routeEditLegLabels = [];
// Set briefly inside insertEditVertexOnSegment; consumed by the
// map-click handler on the very next click event. Stops an
// insert-on-leg from also appending the point at the end of the
// route via the map-click fallback. A flag rather than Leaflet's
// stopPropagation because Leaflet's map click is a separate dispatch
// channel that DOM-level stopPropagation doesn't intercept.
let routeEditSuppressNextMapClick = false;
export function init(map, deps) {
    mapRef = map;
    colors = deps.colors;
    pointToSegmentPixels = deps.pointToSegmentPixels;
}

export function isActive() { return routeEditMode; }
export function consumeSuppressNextMapClick() {
    if (!routeEditSuppressNextMapClick) return false;
    routeEditSuppressNextMapClick = false;
    return true;
}

function makeEditWpIcon(num) {
    return L.divIcon({
        className: 'edit-wp-icon',
        html: `<div class="edit-wp-circle">${num}</div>`,
        iconSize: [24, 24],
        iconAnchor: [12, 12]
    });
}

// rAF-coalesced redraw used by the drag handler below. Marker drag
// fires at touch/pointer-move rate (~60 Hz on a desktop, often higher
// under iPad gesture refinement); calling redrawEditLine directly
// from every drag re-projected every waypoint of the polyline AND
// the wider hit-overlay polyline at 60 Hz. The rAF gate collapses
// multiple drag events between frames into a single redraw on the
// next paint, which is the highest useful update rate anyway.
// Non-drag callers (insertVertex, rebuildMarkers, addWaypoint,
// removeWaypoint, reverseRoute) keep calling redrawEditLine directly
// because they're user-discrete events that want immediate paint.
let _redrawEditLineScheduled = false;
function scheduleRedrawEditLine() {
    if (_redrawEditLineScheduled) return;
    _redrawEditLineScheduled = true;
    requestAnimationFrame(() => {
        _redrawEditLineScheduled = false;
        redrawEditLine();
    });
}

// Compact leg-label format: "850 m" under 1 km, "1.23 nm" above. The
// drag-delta tooltip uses the same threshold so the units stay
// consistent across the edit overlay.
function formatLegDistance(meters) {
    return meters < 1000
        ? `${meters.toFixed(0)} m`
        : `${(meters * NM_PER_METER).toFixed(2)} nm`;
}

function clearLegLabels() {
    if (routeEditLayer) {
        for (const t of routeEditLegLabels) routeEditLayer.removeLayer(t);
    }
    routeEditLegLabels = [];
}

// Rebuild the per-leg distance labels at each segment midpoint. Cheap
// enough at route-editing scale (< 100 waypoints) to recreate every
// redraw; the alternative (track per-segment tooltip refs and update
// in place) would need to handle insert / remove / reverse which
// already correctly trigger a full redraw via rebuildRouteEditMarkers.
function rebuildLegLabels() {
    clearLegLabels();
    if (!routeEditLayer || routeEditCoords.length < 2) return;
    for (let i = 1; i < routeEditCoords.length; i++) {
        const a = routeEditCoords[i - 1];
        const b = routeEditCoords[i];
        const midLat = (a[0] + b[0]) / 2;
        const midLng = (a[1] + b[1]) / 2;
        const d = haversineMeters(a[0], a[1], b[0], b[1]);
        const t = L.tooltip({
            permanent: true,
            direction: 'center',
            className: 'route-edit-leg-tooltip',
            interactive: false,
        })
            .setLatLng([midLat, midLng])
            .setContent(formatLegDistance(d))
            .addTo(routeEditLayer);
        routeEditLegLabels.push(t);
    }
}

function redrawEditLine() {
    if (!routeEditLine && routeEditCoords.length >= 2) {
        routeEditLine = L.polyline(routeEditCoords, {
            color: colors.current, weight: 2.5, opacity: 0.8, dashArray: '8,6'
        }).addTo(routeEditLayer);
        // Wider, transparent polyline underneath as a chunky hit target.
        // On a touch screen the 2.5 px visible line is almost impossible
        // to tap without a stylus; 20 px invisible overlay fixes that
        // without thickening the rendered line. stopPropagation on both
        // click handlers prevents the map-level handler (which APPENDS
        // at the end of the route) from firing in addition to the
        // insert-between-segment handler.
        // 40 px invisible hitbox: real sailing routes don't zig-zag at
        // 5-10 m scale, so a tap a finger-width off the line almost
        // always means "insert here" rather than "drop a new point
        // over there". Wider hitbox removes the "I tapped the line
        // and got a new endpoint instead of an insert" frustration on
        // iPad.
        routeEditHitLine = L.polyline(routeEditCoords, {
            color: colors.current, weight: 40, opacity: 0, interactive: true,
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
    rebuildLegLabels();
}

// Pick the segment closest to `ll` (pixel distance at current zoom,
// so "close" matches what the user sees), splice the click point in
// as a new vertex, rebuild numbered markers.
function insertEditVertexOnSegment(ll) {
    if (routeEditCoords.length < 2 || !mapRef) return;
    const p = mapRef.latLngToLayerPoint(ll);
    let bestIdx = 0;
    let bestDist = Infinity;
    for (let i = 0; i < routeEditCoords.length - 1; i++) {
        const a = mapRef.latLngToLayerPoint(L.latLng(routeEditCoords[i][0],     routeEditCoords[i][1]));
        const b = mapRef.latLngToLayerPoint(L.latLng(routeEditCoords[i + 1][0], routeEditCoords[i + 1][1]));
        const d = pointToSegmentPixels(p, a, b);
        if (d < bestDist) { bestDist = d; bestIdx = i; }
    }
    // Insert at bestIdx + 1 so the order becomes ... prev, new, next ...
    const insertAt = bestIdx + 1;
    routeEditCoords.splice(insertAt, 0, [ll.lat, ll.lng]);
    // Signal the map-level click handler (fires immediately after
    // this one in Leaflet's dispatch order) to skip the append-on-
    // map-click fallback. Without this the user gets a phantom Nth+1
    // waypoint at the end on every leg click.
    routeEditSuppressNextMapClick = true;
    rebuildRouteEditMarkers();
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

// Attach drag handlers with a "ghost" visual: during drag we leave
// the original position visible as a hollow ghost marker and draw a
// dashed rubber-band line from it to the live cursor, with a tooltip
// showing the delta distance. The "ghost + delta" pattern is what
// helms expect when repositioning a waypoint - the sailor always
// sees "how far from where it was".
function bindEditMarker(marker, idx) {
    attachDragGhost(marker, {
        container: () => routeEditLayer,
        color: () => colors.current,
        icon: { className: 'edit-wp-ghost-icon', html: '<div class="edit-wp-ghost-circle"></div>', size: 24 },
        // Clears the 24 px draggable icon (12 px to its top edge + air).
        tooltipOffset: -18,
        onDrag: (ll) => {
            routeEditCoords[idx] = [ll.lat, ll.lng];
            scheduleRedrawEditLine();
        }
    });
}

export function addEditWaypoint(lat, lon) {
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
    routeEditLayer = L.layerGroup().addTo(mapRef);
}

export function stopRouteEdit() {
    routeEditMode = false;
    if (routeEditLayer && mapRef) mapRef.removeLayer(routeEditLayer);
    routeEditLayer = null;
    routeEditCoords = [];
    routeEditMarkers = [];
    routeEditLine = null;
    routeEditHitLine = null;
    routeEditLegLabels = [];
}

export function getEditRouteCoords() {
    return routeEditCoords;
}

// Reverse the order of all edit waypoints in place. Used when the
// user wants to flip a route's direction (e.g. they planned outbound
// and now need the return leg). The marker numbers and the polyline
// are rebuilt from the reversed coord array.
export function reverseEditRoute() {
    if (routeEditCoords.length < 2) return;
    routeEditCoords.reverse();
    rebuildRouteEditMarkers();
    redrawEditLine();
}

// Remove a specific waypoint by index (called from the in-panel list).
// Removing the middle of an N-point route means every subsequent
// marker's number changes, so we tear down the dragging markers and
// rebuild from the coord array. The polyline is re-used (setLatLngs)
// for cheapness.
export function removeRouteEditWaypoint(index) {
    if (index < 0 || index >= routeEditCoords.length) return;
    routeEditCoords.splice(index, 1);
    if (routeEditCoords.length < 2 && routeEditLine && routeEditLayer) {
        routeEditLayer.removeLayer(routeEditLine);
        if (routeEditHitLine) routeEditLayer.removeLayer(routeEditHitLine);
        routeEditLine = null;
        routeEditHitLine = null;
    }
    rebuildRouteEditMarkers();
}

// No route stats here: C# RouteEditing.UpdateRouteStats
// derives (waypointCount, totalDistanceNm) from getEditRouteCoords +
// RouteProgress.TotalDistanceMeters. One haversine sum, in C#.

// Load an existing saved route into edit mode for editing.
export function loadRouteForEdit(coords) {
    stopRouteEdit();
    routeEditMode = true;
    routeEditLayer = L.layerGroup().addTo(mapRef);
    for (const c of coords) {
        addEditWaypoint(c[0], c[1]);
    }
}

export function dispose() {
    routeEditMode = false;
    routeEditLayer = null;
    routeEditCoords = [];
    routeEditMarkers = [];
    routeEditLine = null;
    routeEditHitLine = null;
    routeEditLegLabels = [];
    routeEditSuppressNextMapClick = false;
    mapRef = null;
    colors = null;
    pointToSegmentPixels = null;
}
