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

export function init(map, deps) {
    mapRef = map;
    colors = deps.colors;
}

// Draw/update course line: bearing line + XTE tick.
export function setCourseLine(boatLat, boatLon, wpLat, wpLon, prevLat, prevLon, xteMeters, xteSeverity) {
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
    const brgCoords = [[boatLat, boatLon], [wpLat, wpLon]];
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
        const tickEnd = destPoint(boatLat, boatLon, perpBrg, tickLen);
        const xteCoords = [[boatLat, boatLon], tickEnd];

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
}

export function clearCourseLine() {
    if (courseLineLeg && mapRef) { mapRef.removeLayer(courseLineLeg); courseLineLeg = null; }
    if (courseLineBearing && mapRef) { mapRef.removeLayer(courseLineBearing); courseLineBearing = null; }
    if (courseLineXte && mapRef) { mapRef.removeLayer(courseLineXte); courseLineXte = null; }
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
}

export function dispose() {
    courseLineLeg = null;
    courseLineBearing = null;
    courseLineXte = null;
    mapRef = null;
    colors = null;
}
