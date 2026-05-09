// Route-edit overlay: dashed polyline + numbered draggable waypoint
// markers + a wide invisible hit polyline for finger-tap insertion of
// a vertex on an existing leg. Drag of a waypoint shows a "ghost"
// marker at the original position and a delta tooltip so the helm
// always sees "how far from where it was".
//
// Undo semantics: each addEditWaypoint or insertEditVertexOnSegment
// pushes the inserted index onto routeEditAddStack. Undo pops the
// most-recent index and splices that vertex out - so "undo last
// thing I added" works for both append and mid-route insert (the
// previous "pop the last coord" behaviour was wrong on inserts).

import { haversineMeters, NM_PER_METER } from './geoMath.js';

let mapRef = null;
let colors = null;
let pointToSegmentPixels = null;

let routeEditMode = false;
let routeEditLayer = null;
let routeEditCoords = [];
let routeEditMarkers = [];
let routeEditLine = null;
let routeEditHitLine = null;
// Set briefly inside insertEditVertexOnSegment; consumed by the
// map-click handler on the very next click event. Stops an
// insert-on-leg from also appending the point at the end of the
// route via the map-click fallback. A flag rather than Leaflet's
// stopPropagation because Leaflet's map click is a separate dispatch
// channel that DOM-level stopPropagation doesn't intercept.
let routeEditSuppressNextMapClick = false;
// Undo history. Each entry is the index of the most recently ADDED
// waypoint (append OR mid-route insert).
let routeEditAddStack = [];

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
            color: colors.current, weight: 1.5, opacity: 0.7, dashArray: '3,4',
            interactive: false
        }).addTo(routeEditLayer);
        // Bind once; setTooltipContent on each drag event is cheaper
        // than rebinding a fresh tooltip at ~60 Hz during a long drag.
        // We reposition explicitly via setLatLng, so no `sticky` needed.
        ghostLine.bindTooltip('Δ 0 m', {
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
            const label = dm < 1000 ? `Δ ${dm.toFixed(0)} m` : `Δ ${(dm * NM_PER_METER).toFixed(2)} nm`;
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

export function addEditWaypoint(lat, lon) {
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
    // stack is empty (happens after a loadRouteForEdit hydrate -
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
// Removing the middle of an N-point route means every subsequent
// marker's number changes, so we tear down the dragging markers and
// rebuild from the coord array. The polyline is re-used (setLatLngs)
// for cheapness.
export function removeRouteEditWaypoint(index) {
    if (index < 0 || index >= routeEditCoords.length) return;
    routeEditCoords.splice(index, 1);
    // Shift any add-stack entries. Anything at >index drops one;
    // anything == index is dropped (the user explicitly removed it
    // via the in-panel list, not via undo).
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

// getEditRouteStats removed: C# RouteEditing.UpdateRouteStats now
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
    // The loaded waypoints weren't "added" in this edit session -
    // the user didn't tap them here, they came from the server.
    // Clear the stack so Undo only removes vertices the user added
    // AFTER opening the existing route for edit.
    routeEditAddStack = [];
}

export function dispose() {
    routeEditMode = false;
    routeEditLayer = null;
    routeEditCoords = [];
    routeEditMarkers = [];
    routeEditLine = null;
    routeEditHitLine = null;
    routeEditSuppressNextMapClick = false;
    routeEditAddStack = [];
    mapRef = null;
    colors = null;
    pointToSegmentPixels = null;
}
