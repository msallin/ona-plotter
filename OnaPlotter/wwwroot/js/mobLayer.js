// MOB (Man Overboard) overlay: large pulsing red marker at the
// dropped position, an alarm radius circle, a dashed line to own
// boat, a persistent center-label, plus a two-tone audio chime so
// the helm hears the action confirm even when looking overboard.
//
// Geometry stays live: each updateBoatPosition tick refreshes the
// boat<->MOB line and the midpoint label so the helm sees bearing /
// distance evolve as they manoeuvre back to the casualty.

import { haversineMeters, bearingDeg, NM_PER_METER } from './geoMath.js';

const mobIcon = L.divIcon({
    className: 'mob-icon',
    html: '<div class="mob-pulse"></div>',
    iconSize: [20, 20],
    iconAnchor: [10, 10]
});

let mapRef = null;
let colors = null;

let mobMarker = null;
let mobCircle = null;
let mobLine = null;
let mobLabel = null;

// Latest own-boat position. Pushed from the mux on every updatePosition
// tick so the boat<->MOB line + label can refresh without the module
// reaching back into leafletInterop state.
let selfLat = 0, selfLon = 0;

export function init(map, deps) {
    mapRef = map;
    colors = deps.colors;
}

// Mux pushes the latest own-boat position. While a MOB is active, the
// dashed line and the bearing/distance label re-anchor on the boat
// end so the helm sees their progress back to the casualty live.
export function setBoatPosition(lat, lon) {
    selfLat = lat;
    selfLon = lon;
    if (!mobMarker) return;
    const mll = mobMarker.getLatLng();
    const dist = haversineMeters(lat, lon, mll.lat, mll.lng) * NM_PER_METER;
    const brg = bearingDeg(lat, lon, mll.lat, mll.lng);
    if (mobLine) mobLine.setLatLngs([[lat, lon], [mll.lat, mll.lng]]);
    if (mobLabel) {
        mobLabel.setLatLng([(lat + mll.lat) / 2, (lon + mll.lng) / 2]);
        mobLabel.setContent(`${brg.toFixed(0)}&deg; / ${dist.toFixed(2)} nm`);
    }
}

export function setMob(lat, lon) {
    if (!mapRef) return;  // page unmounted mid-dispatch; same guard as setAnchor.
    clearMob();
    mobMarker = L.marker([lat, lon], { icon: mobIcon, zIndexOffset: 2000 }).addTo(mapRef);
    mobCircle = L.circle([lat, lon], {
        radius: 50, color: colors.mob, fillColor: colors.mob,
        fillOpacity: 0.15, weight: 2
    }).addTo(mapRef);
    mobLine = L.polyline([[selfLat, selfLon], [lat, lon]], {
        color: colors.mob, weight: 2, dashArray: '4,4'
    }).addTo(mapRef);
    mobLabel = L.tooltip({ permanent: true, direction: 'center', className: 'mob-tooltip' })
        .setLatLng([(selfLat + lat) / 2, (selfLon + lon) / 2])
        .setContent('MOB')
        .addTo(mapRef);
    // Audible confirmation: the helm may have been looking overboard
    // when they pressed the button and can't see the pulse animation.
    // Two-tone chime (880/660 Hz, same palette as the connection
    // alarm, but once-only). Inline AudioContext so setMob doesn't
    // depend on MainLayout's module reference; AudioContext is cheap
    // to spin up and is garbage-collected when this scope ends.
    try { playMobChime(); } catch (_) { /* audio blocked in context */ }
}

function playMobChime() {
    const Ctx = window.AudioContext || window.webkitAudioContext;
    if (!Ctx) return;
    const ctx = new Ctx();
    if (ctx.state === 'suspended') ctx.resume();
    const beep = (freq, atSec, durMs) => {
        const osc = ctx.createOscillator();
        const gain = ctx.createGain();
        osc.connect(gain); gain.connect(ctx.destination);
        osc.type = 'square';
        osc.frequency.value = freq;
        gain.gain.setValueAtTime(0.18, ctx.currentTime + atSec);
        gain.gain.exponentialRampToValueAtTime(0.001, ctx.currentTime + atSec + durMs / 1000);
        osc.start(ctx.currentTime + atSec);
        osc.stop(ctx.currentTime + atSec + durMs / 1000);
    };
    beep(880, 0,    220);
    beep(660, 0.26, 220);
    beep(880, 0.54, 260);
    // Close the context shortly after the last note so the ~1s lifetime
    // doesn't linger. Safari occasionally warns about >6 live contexts.
    setTimeout(() => { try { ctx.close(); } catch (_) {} }, 1200);
}

export function clearMob() {
    if (!mapRef) {
        mobMarker = null; mobCircle = null; mobLine = null; mobLabel = null;
        return;
    }
    if (mobMarker) { mapRef.removeLayer(mobMarker); mobMarker = null; }
    if (mobCircle) { mapRef.removeLayer(mobCircle); mobCircle = null; }
    if (mobLine) { mapRef.removeLayer(mobLine); mobLine = null; }
    if (mobLabel) { mapRef.removeLayer(mobLabel); mobLabel = null; }
}

export function dispose() {
    mobMarker = null;
    mobCircle = null;
    mobLine = null;
    mobLabel = null;
    selfLat = 0; selfLon = 0;
    mapRef = null;
    colors = null;
}
