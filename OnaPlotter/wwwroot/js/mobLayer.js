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
// Permanent "MOB hh:mm:ss / lat, lon" label anchored at the dropped
// position. Helm-feedback: the same info used to live in a 30 s
// toast at the bottom-right; helms wanted it pinned to the chart
// where the casualty is so they can read it off the map directly
// while talking on the VHF rather than glancing between two
// places.
let mobPointLabel = null;

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

export function setMob(lat, lon, createdAtIso) {
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
    // Midpoint label: starts as "MOB" and switches to bearing /
    // distance on every boat-position update. Used during the
    // return-to-casualty manoeuvre so the helm sees the live
    // closing geometry while looking at the chart.
    mobLabel = L.tooltip({ permanent: true, direction: 'center', className: 'mob-tooltip' })
        .setLatLng([(selfLat + lat) / 2, (selfLon + lon) / 2])
        .setContent('MOB')
        .addTo(mapRef);
    // At-pin label: time-of-drop + lat / lon. Pinned to the MOB
    // location so the helm can read the casualty fix straight off
    // the chart while reading the VHF mic rather than pulling it
    // from a toast at the bottom-right.
    // createdAtIso: server-stamped ISO-8601 raise time. Falling
    // back to new Date() only when the server didn't supply one
    // (pre-v2 SK server) -- the C# layer hands us the parsed
    // createdAt as ISO so every plotter shows the same minute-
    // and-second on the casualty.
    let ts;
    if (createdAtIso) {
        const parsed = new Date(createdAtIso);
        ts = isNaN(parsed.getTime()) ? new Date() : parsed;
    } else {
        ts = new Date();
    }
    const hh = String(ts.getHours()).padStart(2, '0');
    const mm = String(ts.getMinutes()).padStart(2, '0');
    const ss = String(ts.getSeconds()).padStart(2, '0');
    const ns = lat >= 0 ? 'N' : 'S';
    const ew = lon >= 0 ? 'E' : 'W';
    const labelHtml = `<strong>MOB ${hh}:${mm}:${ss}</strong><br>` +
                      `${Math.abs(lat).toFixed(5)}&deg;${ns} ${Math.abs(lon).toFixed(5)}&deg;${ew}`;
    mobPointLabel = L.tooltip({
        permanent: true,
        direction: 'right',
        offset: [12, 0],
        className: 'mob-point-label',
    })
        .setLatLng([lat, lon])
        .setContent(labelHtml)
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
        mobMarker = null; mobCircle = null; mobLine = null;
        mobLabel = null; mobPointLabel = null;
        return;
    }
    if (mobMarker) { mapRef.removeLayer(mobMarker); mobMarker = null; }
    if (mobCircle) { mapRef.removeLayer(mobCircle); mobCircle = null; }
    if (mobLine) { mapRef.removeLayer(mobLine); mobLine = null; }
    if (mobLabel) { mapRef.removeLayer(mobLabel); mobLabel = null; }
    if (mobPointLabel) { mapRef.removeLayer(mobPointLabel); mobPointLabel = null; }
}

export function dispose() {
    mobMarker = null;
    mobCircle = null;
    mobLine = null;
    mobLabel = null;
    mobPointLabel = null;
    selfLat = 0; selfLon = 0;
    mapRef = null;
    colors = null;
}
