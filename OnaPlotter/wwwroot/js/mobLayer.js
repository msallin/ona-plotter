// MOB (Man Overboard) overlay: large pulsing red marker at the
// dropped position, an alarm radius circle, a dashed line to own
// boat, a persistent center-label, plus a two-tone audio chime so
// the helm hears the action confirm even when looking overboard.
//
// Geometry stays live: each updateBoatPosition tick refreshes the
// boat<->MOB line and the midpoint label so the helm sees bearing /
// distance evolve as they manoeuvre back to the casualty.

import { haversineMeters, bearingDeg, NM_PER_METER } from './geoMath.js';
import { mobElapsed, latDms, lonDms, latLonDms } from './format.js';

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
// Permanent "MOB hh:mm:ss T+m" label anchored at the dropped
// position. Helm-feedback: the same info used to live in a 30 s
// toast at the bottom-right; helms wanted it pinned to the chart
// where the casualty is so they can read it off the map directly
// while talking on the VHF rather than glancing between two
// places. Refreshed every 30 s by elapsedTickHandle so the T+
// minute counter ticks live.
let mobPointLabel = null;
let elapsedTickHandle = null;

// Snapshot of the active MOB so the bound popup builder can
// re-render fresh content (T+ elapsed, current position) every
// time the popup opens, and so a cross-module dotNetRef call
// (Share, GO) reads the same coords the marker shows.
let currentMob = null;   // { lat, lon, createdAt: Date|null, selfMmsi: string|null }

// Bridge to C# for popup-button clicks. Set by leafletInterop on
// init; mobLayer doesn't know the page's DotNetObjectReference
// directly so we route through a getter the host owns.
let getDotNetRef = null;

// Latest own-boat position. Pushed from the mux on every updatePosition
// tick so the boat<->MOB line + label can refresh without the module
// reaching back into leafletInterop state.
let selfLat = 0, selfLon = 0;

export function init(map, deps) {
    mapRef = map;
    colors = deps.colors;
    getDotNetRef = deps.getDotNetRef ?? null;
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

export function setMob(lat, lon, createdAtIso, selfMmsi) {
    if (!mapRef) return;  // page unmounted mid-dispatch; same guard as setAnchor.
    clearMob();
    // createdAtIso: server-stamped ISO-8601 raise time. Falling
    // back to new Date() only when the server didn't supply one
    // (pre-v2 SK server) -- the C# layer hands us the parsed
    // createdAt as ISO so every plotter shows the same minute-
    // and-second on the casualty.
    let createdAt;
    if (createdAtIso) {
        const parsed = new Date(createdAtIso);
        createdAt = isNaN(parsed.getTime()) ? new Date() : parsed;
    } else {
        createdAt = new Date();
    }
    currentMob = { lat, lon, createdAt, selfMmsi: selfMmsi || null };

    mobMarker = L.marker([lat, lon], { icon: mobIcon, zIndexOffset: 2000 }).addTo(mapRef);
    // Bind the rich theme-styled dialog to the marker. Function
    // form so each open re-renders the T+ elapsed counter from
    // currentMob.createdAt against the live clock instead of
    // baking a stale snapshot at marker-creation time.
    mobMarker.bindPopup(buildMobPopupHtml, {
        className: 'mob-popup',
        maxWidth: 280,
        autoPan: true,
        autoPanPadding: [24, 24],
        keepInView: true,
    });
    mobMarker.on('popupopen', wireMobPopupActions);

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
    // At-pin label: time-of-drop + T+ elapsed + lat / lon. Pinned
    // to the MOB location so the helm can read the casualty fix
    // straight off the chart while reading the VHF mic rather than
    // pulling it from a toast at the bottom-right. T+ ticks live
    // via elapsedTickHandle below.
    mobPointLabel = L.tooltip({
        permanent: true,
        direction: 'right',
        offset: [12, 0],
        className: 'mob-point-label',
    })
        .setLatLng([lat, lon])
        .setContent(buildMobLabelHtml())
        .addTo(mapRef);

    // T+ counter ticks every 10 s on the at-pin label (and on any
    // open popup, indirectly -- closing + reopening rebuilds the
    // popup HTML against the same currentMob). 10 s gives the helm
    // a near-live readout of elapsed time without flooding the
    // redraw loop; helm-feedback was that 30 s felt sluggish during
    // the active rescue window.
    if (elapsedTickHandle) clearInterval(elapsedTickHandle);
    elapsedTickHandle = setInterval(() => {
        if (mobPointLabel) mobPointLabel.setContent(buildMobLabelHtml());
        // If the popup is open, refresh its content too so T+
        // updates without the helm having to close + re-tap.
        if (mobMarker && mobMarker.isPopupOpen()) {
            mobMarker.setPopupContent(buildMobPopupHtml());
        }
    }, 10_000);

    // Audible confirmation: the helm may have been looking overboard
    // when they pressed the button and can't see the pulse animation.
    // Two-tone chime (880/660 Hz, same palette as the connection
    // alarm, but once-only). Inline AudioContext so setMob doesn't
    // depend on MainLayout's module reference; AudioContext is cheap
    // to spin up and is garbage-collected when this scope ends.
    try { playMobChime(); } catch (_) { /* audio blocked in context */ }
}

// "T+5m" elapsed-since-raise formatter lives in C# (Format.MobElapsed,
// tested) and is mirrored in format.js. We bridge the JS-side Date
// arithmetic to seconds here so format.js stays time-source agnostic.
function formatElapsed(createdAt) {
    if (!createdAt) return '';
    return mobElapsed((Date.now() - createdAt.getTime()) / 1000);
}

function pad2(n) { return String(n).padStart(2, '0'); }

// At-pin label: short, glanceable -- "MOB HH:MM:SS / T+12m / lat / lon"
function buildMobLabelHtml() {
    if (!currentMob) return '';
    const { lat, lon, createdAt } = currentMob;
    const hh = pad2(createdAt.getHours());
    const mm = pad2(createdAt.getMinutes());
    const ss = pad2(createdAt.getSeconds());
    const elapsed = formatElapsed(createdAt);
    return `<strong>MOB ${hh}:${mm}:${ss}</strong> <span class="mob-elapsed">${elapsed}</span><br>` +
           latLonDms(lat, lon);
}

// Popup: full dialog -- time + T+ + position + MMSI + GO + Share.
// Theme-styled via .mob-popup CSS so the dark / light / high-
// contrast palettes flow through automatically.
function buildMobPopupHtml() {
    if (!currentMob) return '';
    const { lat, lon, createdAt, selfMmsi } = currentMob;
    const hh = pad2(createdAt.getHours());
    const mm = pad2(createdAt.getMinutes());
    const ss = pad2(createdAt.getSeconds());
    const elapsed = formatElapsed(createdAt);
    const mmsiRow = selfMmsi
        ? `<tr><td>MMSI</td><td>${selfMmsi}</td></tr>`
        : '';
    return `
        <div class="mob-popup-body">
            <div class="mob-popup-title">MOB <span class="mob-popup-elapsed">${elapsed}</span></div>
            <table class="mob-popup-table">
                <tr><td>Time</td><td>${hh}:${mm}:${ss}</td></tr>
                <tr><td>Lat</td><td>${latDms(lat)}</td></tr>
                <tr><td>Lon</td><td>${lonDms(lon)}</td></tr>
                ${mmsiRow}
            </table>
            <div class="mob-popup-actions">
                <button class="mob-popup-go-btn" type="button" data-ona-mob-go="1">GO</button>
                <button class="mob-popup-share-btn" type="button" data-ona-mob-share="1">Share</button>
            </div>
        </div>`;
}

function wireMobPopupActions(ev) {
    const el = ev.popup.getElement();
    if (!el) return;
    const goBtn = el.querySelector('[data-ona-mob-go]');
    const shareBtn = el.querySelector('[data-ona-mob-share]');
    if (goBtn && !goBtn._wired) {
        goBtn._wired = true;
        goBtn.addEventListener('click', () => {
            if (!currentMob || !mapRef) return;
            // GO = pan + zoom-in to the casualty so the helm sees
            // the marker centred. setView lets us bump the zoom
            // (clamped) without hijacking the helm's manual zoom
            // when no MOB is up.
            const targetZoom = Math.max(mapRef.getZoom(), 15);
            mapRef.setView([currentMob.lat, currentMob.lon], targetZoom);
        });
    }
    if (shareBtn && !shareBtn._wired) {
        shareBtn._wired = true;
        shareBtn.addEventListener('click', async () => {
            if (!currentMob) return;
            const ref = getDotNetRef ? getDotNetRef() : null;
            if (!ref) return;
            try {
                await ref.invokeMethodAsync('MobShare',
                    currentMob.lat, currentMob.lon,
                    currentMob.createdAt ? currentMob.createdAt.toISOString() : null);
            } catch (_) { /* disposed or navigation in flight */ }
        });
    }
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
    if (elapsedTickHandle) { clearInterval(elapsedTickHandle); elapsedTickHandle = null; }
    currentMob = null;
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
    if (elapsedTickHandle) { clearInterval(elapsedTickHandle); elapsedTickHandle = null; }
    currentMob = null;
    mobMarker = null;
    mobCircle = null;
    mobLine = null;
    mobLabel = null;
    mobPointLabel = null;
    selfLat = 0; selfLon = 0;
    mapRef = null;
    colors = null;
    getDotNetRef = null;
}
