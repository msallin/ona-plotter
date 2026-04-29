// Anchor watch overlay: pin, alarm circle, dashed line from boat to
// anchor, and a swing-arc trail of recent boat positions. Two states
// drive the colour:
//   * boat inside the alarm circle -> green ("anchorOk")
//   * boat outside the alarm circle -> red ("anchorDrag")
// The "raising" flag from the C# side dims everything while we wait
// for the server's cleared-anchor delta to land, so the helm sees
// their tap took effect without us optimistically tearing things down
// (which would mask a server-side raise failure).

import { haversineMeters } from './geoMath.js';

const ANCHOR_TRAIL_MINUTES = 60;
const ANCHOR_TRAIL_SAMPLE_MS = 10_000;

let mapRef = null;
let colors = null;          // MapColors-shaped object from the mux

let anchorMarker = null;
let anchorCircle = null;
// Radius line (boat -> anchor). Turn the visual feedback "where is the
// anchor / how much rode is out" into a first-class affordance instead
// of only showing the watch circle.
let anchorRadiusLine = null;
// Swing-arc history: own-boat positions sampled while the anchor is
// set, trimmed to ANCHOR_TRAIL_MINUTES so the captain sees at a glance
// how much water the boat has actually covered on this tide cycle.
const anchorTrail = [];
let anchorTrailLayer = null;

// Latest known own-boat position. Pushed from the mux on every
// updatePosition tick so the trail and the radius overlay can stay
// in sync without the module reaching back into leafletInterop state.
let selfLat = 0, selfLon = 0;

export function init(map, deps) {
    mapRef = map;
    colors = deps.colors;
}

// Mux pushes the latest own-boat position on every updatePosition
// tick so trail sampling, alarm-state evaluation and the boat<->anchor
// line all read from a single fresh snapshot.
export function setBoatPosition(lat, lon) {
    selfLat = lat;
    selfLon = lon;
    if (anchorMarker && anchorCircle) {
        const all = anchorMarker.getLatLng();
        const dist = haversineMeters(lat, lon, all.lat, all.lng);
        const inside = dist <= anchorCircle.getRadius();
        const acColor = inside ? colors.anchorOk : colors.anchorDrag;
        anchorCircle.setStyle({ color: acColor, fillColor: acColor });
    }
    updateAnchorTrail(lat, lon);
}

export function setAnchor(lat, lon, radiusM) {
    // Swallow calls that hit after the Map page unmounted. Blazor's
    // OnDataChanged handler dispatches asynchronously, so an in-flight
    // HandleDataChanged can land here after DisposeAsync -> dispose()
    // already nulled `mapRef`. Without this guard the next addTo()
    // throws "can't access property addLayer" through the console
    // every time the user switches from Map to Dashboard.
    if (!mapRef) return;
    clearAnchor();
    anchorMarker = L.circleMarker([lat, lon], {
        radius: 5, color: colors.anchorOk, fillColor: colors.anchorOk, fillOpacity: 1
    }).addTo(mapRef);
    anchorCircle = L.circle([lat, lon], {
        radius: radiusM, color: colors.anchorOk, fillColor: colors.anchorOk,
        fillOpacity: 0.06, weight: 2, dashArray: '6,4'
    }).addTo(mapRef);
    // Seed the trail with the current boat position so the first segment
    // renders without waiting for ANCHOR_TRAIL_SAMPLE_MS.
    if (selfLat && selfLon) anchorTrail.push({ lat: selfLat, lon: selfLon, t: Date.now() });
    redrawAnchorRadiusOverlay(lat, lon, radiusM);
}

export function clearAnchor() {
    if (!mapRef) {
        anchorMarker = null; anchorCircle = null; anchorTrailLayer = null;
        anchorTrail.length = 0; anchorRadiusLine = null;
        return;
    }
    if (anchorMarker) { mapRef.removeLayer(anchorMarker); anchorMarker = null; }
    if (anchorCircle) { mapRef.removeLayer(anchorCircle); anchorCircle = null; }
    if (anchorTrailLayer) { mapRef.removeLayer(anchorTrailLayer); anchorTrailLayer = null; }
    if (anchorRadiusLine) { mapRef.removeLayer(anchorRadiusLine); anchorRadiusLine = null; }
    anchorTrail.length = 0;
}

// Visually mark the anchor as "raising" while we wait for the server's
// cleared-anchor delta to land. Dims the marker + watch-circle + radius
// line so the helm sees the action took effect without us optimistically
// hiding the marker (which would mask a server-side raise failure and
// race the next SyncServerAnchorAsync tick). The setStyle calls fall
// through to no-op when a layer is null, so it's safe to call before
// or after setAnchor / clearAnchor.
export function setAnchorRaising(raising) {
    if (!mapRef) return;
    if (anchorMarker) {
        anchorMarker.setStyle(raising
            ? { opacity: 0.35, fillOpacity: 0.4 }
            : { opacity: 1.0, fillOpacity: 1.0 });
    }
    if (anchorCircle) {
        anchorCircle.setStyle(raising
            ? { opacity: 0.35, fillOpacity: 0.02, dashArray: '4,6' }
            : { opacity: 1.0, fillOpacity: 0.06, dashArray: '6,4' });
    }
    if (anchorRadiusLine) {
        anchorRadiusLine.setStyle(raising
            ? { opacity: 0.3 }
            : { opacity: 0.7 });
    }
}

export function updateAnchorRadius(radiusM) {
    if (anchorCircle) anchorCircle.setRadius(radiusM);
    if (anchorMarker) {
        const ll = anchorMarker.getLatLng();
        redrawAnchorRadiusOverlay(ll.lat, ll.lng, radiusM);
    }
}

// Boat<->anchor dashed line. Mutate-in-place via setLatLngs so the 1 Hz
// position update doesn't rebuild the SVG path each tick. Guards
// against missing fix and against NaN sensor glitches (a divide-by-zero
// upstream would otherwise leave the polyline in an invalid state and
// break subsequent setLatLngs calls).
function redrawAnchorRadiusOverlay(anchorLat, anchorLon, _radiusM) {
    if (!mapRef) return;
    if (!Number.isFinite(selfLat) || !Number.isFinite(selfLon)
        || !Number.isFinite(anchorLat) || !Number.isFinite(anchorLon)) {
        if (anchorRadiusLine) { mapRef.removeLayer(anchorRadiusLine); anchorRadiusLine = null; }
        return;
    }

    if (!anchorRadiusLine) {
        anchorRadiusLine = L.polyline(
            [[selfLat, selfLon], [anchorLat, anchorLon]],
            { color: colors.anchorOk, weight: 1.5, opacity: 0.7, dashArray: '4,3', interactive: false }
        ).addTo(mapRef);
    } else {
        anchorRadiusLine.setLatLngs([[selfLat, selfLon], [anchorLat, anchorLon]]);
    }
}

function updateAnchorTrail(lat, lon) {
    if (!mapRef) return;  // page unmounted; skip rather than dereference a null map.
    if (!anchorMarker) {
        // Anchor not set: tear down any residual trail.
        if (anchorTrailLayer) { mapRef.removeLayer(anchorTrailLayer); anchorTrailLayer = null; }
        anchorTrail.length = 0;
        return;
    }

    const now = Date.now();
    const last = anchorTrail[anchorTrail.length - 1];
    if (!last || now - last.t >= ANCHOR_TRAIL_SAMPLE_MS) {
        anchorTrail.push({ lat, lon, t: now });
    } else {
        // Within the sample window - update the latest point so the trail
        // head follows the boat smoothly.
        last.lat = lat; last.lon = lon;
    }

    // Drop points outside the rolling window.
    const cutoff = now - ANCHOR_TRAIL_MINUTES * 60_000;
    while (anchorTrail.length > 0 && anchorTrail[0].t < cutoff) anchorTrail.shift();

    // Keep the radius line chasing the boat as it drifts. The anchor
    // position itself is static (set once) but the line endpoint
    // shifts each tick.
    if (anchorMarker) {
        const a = anchorMarker.getLatLng();
        const r = anchorCircle ? anchorCircle.getRadius() : 0;
        redrawAnchorRadiusOverlay(a.lat, a.lng, r);
    }

    if (anchorTrail.length < 2) return;
    const coords = anchorTrail.map(p => [p.lat, p.lon]);
    if (!anchorTrailLayer) {
        anchorTrailLayer = L.polyline(coords, {
            color: colors.anchorOk, weight: 2, opacity: 0.55,
            dashArray: '2,4', interactive: false
        }).addTo(mapRef);
    } else {
        anchorTrailLayer.setLatLngs(coords);
    }
}

export function dispose() {
    anchorMarker = null;
    anchorCircle = null;
    anchorTrailLayer = null;
    anchorRadiusLine = null;
    anchorTrail.length = 0;
    selfLat = 0; selfLon = 0;
    mapRef = null;
    colors = null;
}
