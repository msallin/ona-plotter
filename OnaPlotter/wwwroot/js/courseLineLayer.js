// Course line overlay drawn on every position update when the helm is
// navigating an active course. Three layers:
//   * bearing line (boat -> next WP, dashed)
//   * leg line (last reached WP -> next WP, kept null in current
//     build; helm reported the original "where I started" stroke as
//     visual noise so it's torn down on every redraw rather than
//     populated)
//   * XTE perpendicular tick at boat position, coloured by severity
//
// Severity bands are classified C#-side (Utilities/Xte.cs +
// XteTests) so the legend, alarm pipeline and this overlay share a
// single set of band thresholds. A single edit in :root cascades to
// the legend and the tick.

import { RAD, bearingDeg, destPoint } from './geoMath.js';

let mapRef = null;
let colors = null;

let courseLineLeg = null;
let courseLineBearing = null;
let courseLineXte = null;
// Pulsing marker at the destination waypoint. Drawn here (not just
// in activeRouteLayer.js) so the "Navigate Here" flow -- which
// drops a course destination but never creates a route -- still
// gets the active-WP visual cue. When a route IS active, the
// activeRouteLayer's nextWpMarker pulses at the same coord with a
// higher zIndex; both are L.divIcon-based with the same CSS
// .active-wp-pulse animation, so a coincident render reads as a
// single pulse rather than two.
let courseLinePulse = null;
// Helm-configured arrival-radius circle around the destination WP.
// Hidden when ArrivalRadiusMeters <= 0 (the helm explicitly
// disables the APPROACH alarm by setting the radius to 0; the
// route HUD shows a "APPROACH alarm off" banner in that case).
let courseLineArrivalRing = null;
const courseLinePulseIcon = L.divIcon({
    className: 'active-wp-icon',
    html: '<div class="active-wp-pulse"></div>',
    iconSize: [16, 16],
    iconAnchor: [8, 8],
});

export function init(map, deps) {
    mapRef = map;
    colors = deps.colors;
}

// Draw/update course line: bearing line + XTE tick + arrival-radius
// ring around the destination.
export function setCourseLine(selfLat, selfLon, wpLat, wpLon, prevLat, prevLon, xteMeters, xteSeverity, arrivalRadiusMeters) {
    if (!mapRef) return;

    // Tear down any leftover leg line from a previous build that
    // still emitted it. courseLineLeg stays declared at module scope
    // so dispose() can null it; this just guarantees the layer is
    // gone if some older state left it behind.
    if (courseLineLeg) {
        mapRef.removeLayer(courseLineLeg);
        courseLineLeg = null;
    }

    // Bearing line: boat to next WP.
    const brgCoords = [[selfLat, selfLon], [wpLat, wpLon]];
    if (courseLineBearing) {
        courseLineBearing.setLatLngs(brgCoords);
    } else {
        courseLineBearing = L.polyline(brgCoords, {
            color: colors.bearing, weight: 2, opacity: 0.7, dashArray: '6,4'
        }).addTo(mapRef);
    }

    // XTE perpendicular tick at boat position.
    if (xteMeters != null && prevLat != null && prevLon != null) {
        const absXte = Math.abs(xteMeters);
        const xteColor = xteSeverity === 'offCourse' ? colors.mob
            : xteSeverity === 'drifting' ? colors.guardWarn
            : colors.anchorOk;
        // Perpendicular to the leg bearing.
        const legBrg = bearingDeg(prevLat, prevLon, wpLat, wpLon) * RAD;
        const perpBrg = xteMeters > 0 ? legBrg + Math.PI / 2 : legBrg - Math.PI / 2;
        // Visual length: actual XTE capped at 200m for display.
        const tickLen = Math.min(absXte, 200);
        const tickEnd = destPoint(selfLat, selfLon, perpBrg, tickLen);
        const xteCoords = [[selfLat, selfLon], tickEnd];

        if (courseLineXte) {
            courseLineXte.setLatLngs(xteCoords);
            courseLineXte.setStyle({ color: xteColor });
        } else {
            courseLineXte = L.polyline(xteCoords, {
                color: xteColor, weight: 3, opacity: 0.9
            }).addTo(mapRef);
        }
    } else if (courseLineXte) {
        mapRef.removeLayer(courseLineXte);
        courseLineXte = null;
    }

    // Pulse marker at the destination. Re-position when present;
    // create when absent. zIndexOffset 800 -- below the active-route
    // layer's nextWpMarker (900) so a route-active render lets the
    // route's marker (with its "WP N" tooltip) win when they collide.
    if (courseLinePulse) {
        courseLinePulse.setLatLng([wpLat, wpLon]);
    } else {
        courseLinePulse = L.marker([wpLat, wpLon], {
            icon: courseLinePulseIcon,
            zIndexOffset: 800,
            interactive: false,
        }).addTo(mapRef);
    }

    // Arrival-radius ring at the destination. L.circle takes a radius
    // in metres and projects properly across zooms, so the ring
    // always represents the helm-configured "arrived" distance to
    // scale. Radius 0 (or negative) means the helm disabled the
    // APPROACH alarm; tear down the ring in that case so the chart
    // doesn't suggest an alarm that won't fire.
    if (typeof arrivalRadiusMeters === 'number' && arrivalRadiusMeters > 0) {
        if (courseLineArrivalRing) {
            courseLineArrivalRing.setLatLng([wpLat, wpLon]);
            courseLineArrivalRing.setRadius(arrivalRadiusMeters);
        } else {
            courseLineArrivalRing = L.circle([wpLat, wpLon], {
                radius: arrivalRadiusMeters,
                color: colors.bearing,
                weight: 1,
                opacity: 0.55,
                dashArray: '4,4',
                fill: false,
                interactive: false,
            }).addTo(mapRef);
        }
    } else if (courseLineArrivalRing) {
        mapRef.removeLayer(courseLineArrivalRing);
        courseLineArrivalRing = null;
    }
}

export function clearCourseLine() {
    if (courseLineLeg && mapRef) { mapRef.removeLayer(courseLineLeg); courseLineLeg = null; }
    if (courseLineBearing && mapRef) { mapRef.removeLayer(courseLineBearing); courseLineBearing = null; }
    if (courseLineXte && mapRef) { mapRef.removeLayer(courseLineXte); courseLineXte = null; }
    if (courseLinePulse && mapRef) { mapRef.removeLayer(courseLinePulse); courseLinePulse = null; }
    if (courseLineArrivalRing && mapRef) { mapRef.removeLayer(courseLineArrivalRing); courseLineArrivalRing = null; }
}

// Dim/restore opacity. Called by setActiveRouteStopping in the mux
// so the helm sees their "Stop Navigation" tap landed without us
// optimistically tearing the lines down (the SK delta drives the
// real teardown).
export function setStoppingDim(stopping) {
    const opacity = stopping ? 0.3 : 1.0;
    if (courseLineLeg) {
        try { courseLineLeg.setStyle({ opacity }); } catch (_) { /* tooltips have no setStyle */ }
    }
    if (courseLineBearing) {
        try { courseLineBearing.setStyle({ opacity }); } catch (_) { }
    }
    if (courseLineXte) {
        try { courseLineXte.setStyle({ opacity }); } catch (_) { }
    }
    if (courseLinePulse) {
        try { courseLinePulse.setOpacity(opacity); } catch (_) { }
    }
    if (courseLineArrivalRing) {
        try { courseLineArrivalRing.setStyle({ opacity }); } catch (_) { }
    }
}

export function dispose() {
    courseLineLeg = null;
    courseLineBearing = null;
    courseLineXte = null;
    courseLinePulse = null;
    courseLineArrivalRing = null;
    mapRef = null;
    colors = null;
}
