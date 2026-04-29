// Sailboat tactical laylines: port + starboard tracks projecting from
// the boat (and optionally back from a target waypoint) at the boat's
// upwind tacking angle. Lets the helm see at a glance where each tack
// would carry her, and where the layline crosses an upwind waypoint.
// Colour follows the anchor-ok / mob palette so green = "safe tack"
// (starboard) and red = "other tack" (port) reads consistently with
// the rest of the chart.

import { destPoint } from './geoMath.js';

let mapRef = null;
let colors = null;

let laylineStarboard = null;    // Green polyline from boat
let laylinePort = null;         // Red polyline from boat
let laylineWpStarboard = null;  // Green polyline from waypoint (dimmer)
let laylineWpPort = null;       // Red polyline from waypoint (dimmer)

export function init(map, deps) {
    mapRef = map;
    colors = deps.colors;
}

// Draw port/starboard laylines from boat position (and optionally from waypoint).
// twdRad = true wind direction (radians, FROM north).
// twaRad = true wind angle (radians, absolute).
export function setLaylines(boatLat, boatLon, twdRad, twaRad, wpLat, wpLon) {
    if (!mapRef) return;

    const lineLen = 5 * 1852; // 5 nm in meters
    const absTwa = Math.abs(twaRad);

    // Boat sails INTO the wind: TWD + PI gives the "to" direction, +/- TWA gives tack angles.
    const stbdBrg = twdRad + Math.PI - absTwa;
    const portBrg = twdRad + Math.PI + absTwa;

    const stbdEnd = destPoint(boatLat, boatLon, stbdBrg, lineLen);
    const portEnd = destPoint(boatLat, boatLon, portBrg, lineLen);

    if (laylineStarboard) laylineStarboard.setLatLngs([[boatLat, boatLon], stbdEnd]);
    else {
        laylineStarboard = L.polyline([[boatLat, boatLon], stbdEnd], {
            color: colors.anchorOk, weight: 2, opacity: 0.7, dashArray: '10,6'
        }).addTo(mapRef);
    }

    if (laylinePort) laylinePort.setLatLngs([[boatLat, boatLon], portEnd]);
    else {
        laylinePort = L.polyline([[boatLat, boatLon], portEnd], {
            color: colors.mob, weight: 2, opacity: 0.7, dashArray: '10,6'
        }).addTo(mapRef);
    }

    // Waypoint laylines (from waypoint back toward the wind).
    if (wpLat != null && wpLon != null) {
        const wpStbdEnd = destPoint(wpLat, wpLon, stbdBrg + Math.PI, lineLen);
        const wpPortEnd = destPoint(wpLat, wpLon, portBrg + Math.PI, lineLen);

        if (laylineWpStarboard) laylineWpStarboard.setLatLngs([[wpLat, wpLon], wpStbdEnd]);
        else {
            laylineWpStarboard = L.polyline([[wpLat, wpLon], wpStbdEnd], {
                color: colors.anchorOk, weight: 1.5, opacity: 0.35, dashArray: '6,6'
            }).addTo(mapRef);
        }
        if (laylineWpPort) laylineWpPort.setLatLngs([[wpLat, wpLon], wpPortEnd]);
        else {
            laylineWpPort = L.polyline([[wpLat, wpLon], wpPortEnd], {
                color: colors.mob, weight: 1.5, opacity: 0.35, dashArray: '6,6'
            }).addTo(mapRef);
        }
    } else {
        if (laylineWpStarboard && mapRef) { mapRef.removeLayer(laylineWpStarboard); laylineWpStarboard = null; }
        if (laylineWpPort && mapRef) { mapRef.removeLayer(laylineWpPort); laylineWpPort = null; }
    }
}

export function clearLaylines() {
    if (laylineStarboard && mapRef) { mapRef.removeLayer(laylineStarboard); laylineStarboard = null; }
    if (laylinePort && mapRef) { mapRef.removeLayer(laylinePort); laylinePort = null; }
    if (laylineWpStarboard && mapRef) { mapRef.removeLayer(laylineWpStarboard); laylineWpStarboard = null; }
    if (laylineWpPort && mapRef) { mapRef.removeLayer(laylineWpPort); laylineWpPort = null; }
}

export function dispose() {
    laylineStarboard = null;
    laylinePort = null;
    laylineWpStarboard = null;
    laylineWpPort = null;
    mapRef = null;
    colors = null;
}
