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
// "Incomplete" state: helm dropped the pin (server has the position)
// but hasn't set the alarm radius yet. Tracked here so setBoatPosition
// keeps the pin pulsing and doesn't flip the circle to the green
// "inside the alarm circle" colour the moment the radius arrives - the
// helm needs to see ON THE CHART that step 2 is still pending.
let anchorIncomplete = false;
// Swing-arc history: own-boat positions sampled while the anchor is
// set, trimmed to ANCHOR_TRAIL_MINUTES so the captain sees at a glance
// how much water the boat has actually covered on this tide cycle.
const anchorTrail = [];
let anchorTrailLayer = null;

// Latest known own-boat position. Pushed from the mux on every
// updatePosition tick so the trail and the radius overlay can stay
// in sync without the module reaching back into leafletInterop state.
let selfLat = 0, selfLon = 0;

// Cached inside/outside bucket from the previous setBoatPosition.
// Anchor-circle restyle (the green<->red flip) only needs to fire on
// the transition; without this cache every tick at anchor wrote a
// setStyle({color, fillColor}) that re-emitted the same value and
// invalidated the SVG/canvas layer for no visible change.
let _lastInside = null;

// Cache key for the boat<->anchor dashed line endpoints. Reset on
// clearAnchor / dispose so a fresh watch redraws on the first fix
// rather than incorrectly believing the previous endpoints are still
// valid. Declared at module top so clearAnchor can reset it without
// tripping the no-use-before-define lint rule.
let _lastRadiusLineKey = null;

// Manual-move state. The helm taps "Move" in the anchor panel, which
// drops a draggable handle on top of the pin; dragging it repositions
// the pin + watch circle + radius line live (visual preview only - the
// PUT to navigation.anchor.position happens C#-side when the helm taps
// Set). `anchorMoveHandle` is the L.marker carrying the drag (a plain
// L.circleMarker has no built-in dragging, so we overlay a real marker
// rather than swap the pin out). `anchorMovedLatLng` holds the latest
// dragged position for C# to read on commit; null when move mode is off.
let anchorMoveHandle = null;
let anchorMovedLatLng = null;

// Min boat-tail movement (m) within the sample window before we
// repaint the trail polyline. GPS noise at rest is bounded ~0.5 m;
// updating the trail head and re-emitting setLatLngs on sub-half-
// metre jitter rebuilds an N-point polyline (N grows to ~360 over
// an hour at 10 s sampling) for a visual delta of zero pixels at
// the zoom levels where the anchor circle is on screen. The next
// sample event (>= ANCHOR_TRAIL_SAMPLE_MS) bypasses this gate so
// the trail still extends on real motion.
const ANCHOR_TRAIL_TAIL_EPSILON_M = 0.5;

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
    // Skip the inside/outside circle recolour while the anchor is in
    // the incomplete state - the alarm circle isn't really armed
    // yet and the green/red colour would mislead. setAnchorIncomplete
    // owns the styling in that state.
    if (anchorMarker && anchorCircle && !anchorIncomplete) {
        const all = anchorMarker.getLatLng();
        const dist = haversineMeters(lat, lon, all.lat, all.lng);
        const inside = dist <= anchorCircle.getRadius();
        // Only restyle on the bucket transition. setStyle is not a
        // no-op when the value matches: Leaflet's vector path writes
        // every style attribute and invalidates the renderer layer
        // even when the resulting paint is identical. At anchor in
        // harbor, inside stays true for the whole watch; the every-
        // tick setStyle was a recurring compositor wake-up.
        if (inside !== _lastInside) {
            _lastInside = inside;
            const acColor = inside ? colors.anchorOk : colors.anchorDrag;
            anchorCircle.setStyle({ color: acColor, fillColor: acColor });
        }
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
    anchorIncomplete = false;
    _lastInside = null;
    _lastRadiusLineKey = null;
    anchorMovedLatLng = null;
    if (!mapRef) {
        anchorMarker = null; anchorCircle = null; anchorTrailLayer = null;
        anchorTrail.length = 0; anchorRadiusLine = null; anchorMoveHandle = null;
        return;
    }
    if (anchorMoveHandle) { mapRef.removeLayer(anchorMoveHandle); anchorMoveHandle = null; }
    if (anchorMarker) { mapRef.removeLayer(anchorMarker); anchorMarker = null; }
    if (anchorCircle) { mapRef.removeLayer(anchorCircle); anchorCircle = null; }
    if (anchorTrailLayer) { mapRef.removeLayer(anchorTrailLayer); anchorTrailLayer = null; }
    if (anchorRadiusLine) { mapRef.removeLayer(anchorRadiusLine); anchorRadiusLine = null; }
    anchorTrail.length = 0;
}

// Visually mark the anchor as "drop committed but radius not yet set"
// (v2.0.0+ two-step flow's intermediate state). Switches the marker
// to amber + the circle to a heavier dash so the chart visibly
// distinguishes "still in step 2" from a fully-armed anchor; the
// AnchorEditPanel SetRadius dialog itself is the helm-facing
// instruction surface. The legacy permanent "RADIUS NOT SET"
// tooltip was helm-flagged as visual noise once the dialog took
// over the same role, so it's gone.
//
// Idempotent: safe to call with the same value, safe to call when no
// anchor is set (the toggles fall through to no-op).
export function setAnchorIncomplete(incomplete) {
    anchorIncomplete = !!incomplete;
    if (!mapRef || !anchorMarker || !anchorCircle) return;
    if (anchorIncomplete) {
        // Heavier dashed ring + amber-ish tone so the chart visibly
        // says "this isn't fully armed yet". Reuse the anchorDrag
        // colour as the visual cue (fallback to amber): drag is the
        // attention-grabbing red, but for a not-yet-armed anchor we
        // want amber-grade attention. Marker class triggers the
        // CSS pulse animation.
        anchorMarker.setStyle({ color: '#f59e0b', fillColor: '#f59e0b', weight: 2 });
        anchorCircle.setStyle({
            color: '#f59e0b', fillColor: '#f59e0b',
            fillOpacity: 0.04, weight: 1.5, dashArray: '2,8'
        });
    } else {
        // Restore the fully-armed look. setBoatPosition will replace
        // the colour on the next tick based on inside/outside the
        // circle; we set anchorOk here so the brief pre-tick render
        // doesn't flash amber.
        anchorMarker.setStyle({ color: colors.anchorOk, fillColor: colors.anchorOk, weight: 1 });
        anchorCircle.setStyle({
            color: colors.anchorOk, fillColor: colors.anchorOk,
            fillOpacity: 0.06, weight: 2, dashArray: '6,4'
        });
    }
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

// Reposition the pin, watch circle and radius line to a new anchor
// position without tearing the overlay down (clearAnchor + setAnchor
// would drop the swing trail and flash the layer). Used for the
// manual-move revert path (helm cancels) and is the natural seam for
// a future server-position re-sync. No-op when no anchor is drawn.
export function setAnchorPosition(lat, lon) {
    if (!mapRef || !anchorMarker) return;
    anchorMarker.setLatLng([lat, lon]);
    if (anchorCircle) anchorCircle.setLatLng([lat, lon]);
    const r = anchorCircle ? anchorCircle.getRadius() : 0;
    redrawAnchorRadiusOverlay(lat, lon, r);
    if (anchorMoveHandle) anchorMoveHandle.setLatLng([lat, lon]);
}

// Toggle manual-move mode. When enabled, overlay a draggable handle
// on the pin; dragging it moves the pin + circle + radius line live so
// the helm sees where the anchor will land before committing. The
// dragged position is stashed in `anchorMovedLatLng` for C# to read on
// Set. When disabled, the handle is removed and the stash cleared;
// callers that disable WITHOUT committing should call setAnchorPosition
// first to snap the visuals back to the server position.
//
// Idempotent: re-enabling while already on is a no-op; disabling when
// off falls through cleanly.
export function setAnchorMoveMode(enable) {
    if (!mapRef) return;
    if (enable) {
        if (anchorMoveHandle || !anchorMarker) return;
        const ll = anchorMarker.getLatLng();
        anchorMovedLatLng = { lat: ll.lat, lon: ll.lng };
        anchorMoveHandle = L.marker(ll, {
            draggable: true,
            keyboard: false,
            zIndexOffset: 1000,
            icon: L.divIcon({
                className: 'anchor-move-handle',
                html: '<div class="anchor-move-handle-dot"></div>',
                iconSize: [30, 30],
                iconAnchor: [15, 15],
            }),
        }).addTo(mapRef);
        anchorMoveHandle.on('drag', (e) => {
            const p = e.target.getLatLng();
            anchorMovedLatLng = { lat: p.lat, lon: p.lng };
            setAnchorPosition(p.lat, p.lng);
        });
    } else {
        if (anchorMoveHandle) { mapRef.removeLayer(anchorMoveHandle); anchorMoveHandle = null; }
        anchorMovedLatLng = null;
    }
}

// Latest dragged position during move mode as {lat, lon}, or null when
// move mode is off / nothing has been dragged. C# reads this on Set to
// decide whether to PUT a new navigation.anchor.position.
export function getAnchorMovedLatLng() {
    return anchorMovedLatLng;
}

// Boat<->anchor dashed line. Mutate-in-place via setLatLngs so the 1 Hz
// position update doesn't rebuild the SVG path each tick. Guards
// against missing fix and against NaN sensor glitches (a divide-by-zero
// upstream would otherwise leave the polyline in an invalid state and
// break subsequent setLatLngs calls).
//
// Endpoint cache (_lastRadiusLineKey, declared at module top): anchor
// lat/lon is static for the watch and selfLat/selfLon usually re-
// arrives identical (or jitters within sub-metre GPS noise).
// setLatLngs reprojects + rewrites the SVG/canvas command stream on
// every call regardless of input - the cache collapses the per-tick
// no-op to a single equality check.
function redrawAnchorRadiusOverlay(anchorLat, anchorLon, _radiusM) {
    if (!mapRef) return;
    if (!Number.isFinite(selfLat) || !Number.isFinite(selfLon)
        || !Number.isFinite(anchorLat) || !Number.isFinite(anchorLon)) {
        if (anchorRadiusLine) { mapRef.removeLayer(anchorRadiusLine); anchorRadiusLine = null; }
        _lastRadiusLineKey = null;
        return;
    }

    // Round to 1e-6 (~11 cm at the equator). Below the displayable
    // delta at any zoom where the anchor circle fits on the chart, so
    // a skipped redraw is invisible. Real motion above the noise floor
    // exceeds this and gets a fresh polyline.
    const key = Math.round(selfLat * 1e6) + ','
              + Math.round(selfLon * 1e6) + ','
              + Math.round(anchorLat * 1e6) + ','
              + Math.round(anchorLon * 1e6);
    if (anchorRadiusLine && _lastRadiusLineKey === key) return;
    _lastRadiusLineKey = key;

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

    // Track whether the trail array structure or any visible coord
    // changed this call; setLatLngs is skipped when it didn't, because
    // re-emitting the same N-point polyline still triggers a vector-
    // layer repaint on Leaflet.
    let trailChanged = false;

    const now = Date.now();
    const last = anchorTrail[anchorTrail.length - 1];
    if (!last || now - last.t >= ANCHOR_TRAIL_SAMPLE_MS) {
        anchorTrail.push({ lat, lon, t: now });
        trailChanged = true;
    } else if (haversineMeters(last.lat, last.lon, lat, lon) >= ANCHOR_TRAIL_TAIL_EPSILON_M) {
        // Within the sample window - update the latest point so the
        // trail head follows the boat smoothly. Gated on a 0.5 m
        // epsilon so GPS-noise jitter at rest doesn't rebuild the
        // polyline on every tick (next real sample event at
        // ANCHOR_TRAIL_SAMPLE_MS will pick up any motion below the
        // epsilon as part of a fresh point anyway).
        last.lat = lat; last.lon = lon;
        trailChanged = true;
    }

    // Drop points outside the rolling window.
    const cutoff = now - ANCHOR_TRAIL_MINUTES * 60_000;
    while (anchorTrail.length > 0 && anchorTrail[0].t < cutoff) {
        anchorTrail.shift();
        trailChanged = true;
    }

    // Keep the radius line chasing the boat as it drifts. The anchor
    // position itself is static (set once) but the line endpoint
    // shifts each tick. redrawAnchorRadiusOverlay caches its own
    // latlng key so the per-tick path is a no-op when the boat
    // hasn't moved.
    if (anchorMarker) {
        const a = anchorMarker.getLatLng();
        const r = anchorCircle ? anchorCircle.getRadius() : 0;
        redrawAnchorRadiusOverlay(a.lat, a.lng, r);
    }

    if (!trailChanged) return;
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
    anchorMoveHandle = null;
    anchorMovedLatLng = null;
    anchorTrail.length = 0;
    selfLat = 0; selfLon = 0;
    _lastInside = null;
    _lastRadiusLineKey = null;
    mapRef = null;
    colors = null;
}
