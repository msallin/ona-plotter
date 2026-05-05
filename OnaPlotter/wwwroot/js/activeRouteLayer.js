// Active route navigation overlay: the route polyline split into
// already-passed (dotted, dim) and future (solid) legs, numbered
// waypoint dots, a pulsing marker at the next waypoint, and a tap-
// targeted popup on the polyline carrying Deactivate / Edit / Delete.
//
// The course line (boat -> next WP bearing + XTE perpendicular tick)
// is drawn separately by courseLineLayer.js -- this module owns the
// route geometry, the other owns the live navigational overlay.
//
// Line-style convention used across the chartplotter:
//   - solid  = "planned" (saved route, or future legs of an active route)
//   - dashed = "ship-to-waypoint" (bearing line, current-leg overlay)
//   - dotted = "already passed" (legs of the active route the boat crossed)

const activeWpIcon = L.divIcon({
    className: 'active-wp-icon',
    html: '<div class="active-wp-pulse"></div>',
    iconSize: [24, 24],
    iconAnchor: [12, 12]
});

let mapRef = null;
let colors = null;
let getDotNetRef = null;
let getEditModeFlags = null;
let editModeAddPoint = null;
let buildActiveRoutePopupHtml = null;
let wireRouteDeactivate = null;
let wireRouteEdit = null;
let wireDeleteConfirm = null;
let routeTotalNauticalMiles = null;
let clearCourseLineFn = null;
let setCoursePulseSuppressedFn = null;

let activeRouteLayer = null;
let activeRouteCoords = null;
let nextWpMarker = null;
let activeOverlayHidden = false;

export function init(map, deps) {
    mapRef = map;
    colors = deps.colors;
    getDotNetRef = deps.getDotNetRef;
    getEditModeFlags = deps.getEditModeFlags;
    editModeAddPoint = deps.editModeAddPoint;
    buildActiveRoutePopupHtml = deps.buildActiveRoutePopupHtml;
    wireRouteDeactivate = deps.wireRouteDeactivate;
    wireRouteEdit = deps.wireRouteEdit;
    wireDeleteConfirm = deps.wireDeleteConfirm;
    routeTotalNauticalMiles = deps.routeTotalNauticalMiles;
    clearCourseLineFn = deps.clearCourseLine;
    setCoursePulseSuppressedFn = deps.setCoursePulseSuppressed;
}

// applyFrame consults this to decide whether to redraw the course
// line on each tick. Hidden while the helm is editing the active
// route so we don't fight the edit-mode polyline.
export function isOverlayHidden() { return activeOverlayHidden; }
export function getActiveRouteCoords() { return activeRouteCoords; }

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
    if (!mapRef || !coords || coords.length < 2) return;

    activeRouteCoords = coords;
    // Defensive bounds-check; C# already clamps but a stray NaN/-1
    // from a future caller must not crash the renderer.
    const idx = Math.max(0, Math.min(coords.length - 1, wpIdx | 0));
    activeRouteLayer = L.layerGroup().addTo(mapRef);
    // Hide the course-line layer's own pulsing destination marker
    // for the duration of this route -- our nextWpMarker (drawn
    // below) covers the same coord with a higher zIndex + a "WP N"
    // tooltip, so two pulses would stack visibly.
    if (setCoursePulseSuppressedFn) setCoursePulseSuppressedFn(true);
    const dotNetRef = getDotNetRef();
    const tappable = !!(routeId && dotNetRef);

    // Already-passed legs: dotted, dim. The two polylines share the
    // boundary point coords[idx-1] so the dotted/solid handover
    // renders without a visual gap. lineCap:'round' turns the 2 px
    // dash into a true round dot, which scans more cleanly than a
    // dash at chartplotter zoom levels.
    if (idx >= 2) {
        L.polyline(coords.slice(0, idx), {
            color: colors.bearing,
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
            color: colors.bearing, weight: 3, opacity: 0.8
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
            color: colors.bearing, weight: 36, opacity: 0, interactive: true,
        }).addTo(activeRouteLayer);
        const nmTotal = routeTotalNauticalMiles(coords);
        const popupOptions = { className: 'route-popup', maxWidth: 320, autoClose: true };
        // Function-form bindPopup: Leaflet calls this each time the
        // popup opens, so the ETA picks up the latest TTG cached from
        // the C# data-tick. Static HTML would have frozen the ETA at
        // route-activation time.
        hitLine.bindPopup(() => buildActiveRoutePopupHtml(routeId, routeName, coords.length, nmTotal), popupOptions);
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
            const flags = getEditModeFlags();
            if (flags.routeEdit || flags.polygonEdit || flags.measure) {
                L.DomEvent.stopPropagation(ev);
                const ll = ev.latlng;
                if (!ll) return;
                // A click ON the active-route polyline is unambiguous:
                // the helm tapped this specific route. Edit-mode
                // dispatch follows the historical priority (route ->
                // polygon -> measure); the measure-first override only
                // applies to empty-map clicks (see
                // leafletInterop.js::map.on('click')).
                if (flags.routeEdit)         editModeAddPoint('route', ll.lat, ll.lng);
                else if (flags.polygonEdit)  editModeAddPoint('polygon', ll.lat, ll.lng);
                else                         editModeAddPoint('measure', ll.lat, ll.lng);
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
            color: colors.bearing,
            fillColor: isPassed ? '#64748b' : colors.bearing,
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
        const dotNetRef = getDotNetRef();
        if (!dotNetRef) return;
        dotNetRef.invokeMethodAsync('JumpToRouteWaypoint', routeId, pointIndex)
            .catch(() => {});
    });
}

export function clearActiveRoute() {
    if (activeRouteLayer && mapRef) { mapRef.removeLayer(activeRouteLayer); }
    activeRouteLayer = null;
    activeRouteCoords = null;
    nextWpMarker = null;
    // Hand the destination pulse back to courseLineLayer so a
    // Navigate-Here destination (without an active route) still
    // gets a visible marker.
    if (setCoursePulseSuppressedFn) setCoursePulseSuppressedFn(false);
}

// Toggle the "edit-active-route in progress" suppression flag. While
// hidden, applyFrame skips setCourseLine (so the leg / bearing / XTE
// tick don't keep redrawing on every position update against stale
// pre-edit geometry) and any stray setActiveRoute call during edit
// is a no-op. Also tears down the course-line elements AND the
// active-route layer (numbered WP dots + the pulsing next-WP marker)
// so the helm's view is clean from the moment edit starts.
//
// Tearing down the next-WP marker matters specifically for drag:
// Leaflet stacks markers by zIndexOffset and our active-WP pulse
// sits at 900 vs the route-edit drag handles' 800. With the pulse
// still on the map during edit mode, pointer events on the
// next-WP coordinate hit the (non-draggable) pulse marker first
// and never reach the underlying drag handle -- the helm taps
// the WP, nothing happens, and the route edit feels broken.
//
// C# pairs every true with a false on edit cancel / save -- the
// next position frame's SyncActiveRouteAsync(force: true) then
// redraws everything from the updated coords.
export function setActiveOverlayHidden(hidden) {
    activeOverlayHidden = !!hidden;
    if (hidden) {
        clearCourseLineFn();
        clearActiveRoute();
    }
}

// Visually mark the active route as "stopping" while we wait for the
// SK delta to confirm. Same single-source-of-truth pattern as
// setAnchorRaising: dim the elements (so the helm sees their tap
// landed) but never tear them down -- the delta drives the real
// teardown via SyncActiveRouteAsync. Iterates the layer group's
// children with setStyle so polyline + waypoint dots all dim
// together. Course-line dimming is delegated to the courseLineLayer.
export function setActiveRouteStoppingPolyline(stopping) {
    if (!mapRef) return;
    const opacity = stopping ? 0.3 : 1.0;
    const fillOpacity = stopping ? 0.3 : 1.0;
    if (activeRouteLayer) {
        activeRouteLayer.eachLayer(function (l) {
            try { l.setStyle({ opacity: opacity, fillOpacity: fillOpacity }); }
            catch (_) { /* tooltips have no setStyle; ignore */ }
        });
    }
}

export function dispose() {
    activeRouteLayer = null;
    activeRouteCoords = null;
    nextWpMarker = null;
    activeOverlayHidden = false;
    mapRef = null;
    colors = null;
    getDotNetRef = null;
    getEditModeFlags = null;
    editModeAddPoint = null;
    buildActiveRoutePopupHtml = null;
    wireRouteDeactivate = null;
    wireRouteEdit = null;
    wireDeleteConfirm = null;
    routeTotalNauticalMiles = null;
    clearCourseLineFn = null;
}
