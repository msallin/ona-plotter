// Standalone waypoint markers (SignalK /resources/waypoints).
// Rendered as a small filled circle with a wider invisible
// hit-buffer so a finger-wide tap registers (44 px iPad WCAG floor)
// while the visible marker stays 6 px to keep the chart legible.
// Hover shows a name + coordinates tooltip; click opens a popup
// with name + Delete (two-step confirm).
//
// MOB waypoints (isMob=true) are a specialty: the active variant
// renders with a pulsing red icon plus an alarm-radius circle, a
// dashed boat<->casualty line, a live midpoint bearing/distance
// label, a pinned at-pin "MOB HH:MM:SS / T+12m / lat / lon" label,
// and a rich popup with GO (pan + zoom in) / Share / MMSI. Cleared
// MOBs (isActive=false) keep the solid red icon as persistent
// history but lose the overlays. Edit + Delete are refused on MOB
// waypoints from both the popup (UI hides the buttons) and the
// JSInvokable trust boundary in C#.

import { MarkerLayer } from './markerLayer.js';
import { esc, wireDeleteConfirm } from './popupHelpers.js';
import { latLonDms, latDms, lonDms, mobElapsed } from './format.js';
import { haversineMeters, bearingDeg, NM_PER_METER } from './geoMath.js';

const waypointMarkers = new MarkerLayer();
let mapRef = null;
let colors = null;
let getDotNetRef = null;
let getEditModeFlags = null;
let editModeAddPoint = null;
let getOwnMmsi = null;

export function init(map, deps) {
    mapRef = map;
    colors = deps.colors;
    getDotNetRef = deps.getDotNetRef;
    getEditModeFlags = deps.getEditModeFlags;
    editModeAddPoint = deps.editModeAddPoint;
    getOwnMmsi = deps.getOwnMmsi ?? null;
    waypointMarkers.setMap(map);
}

// Formats the hover-tooltip content for a waypoint marker: name (or
// short id if unnamed) above a compact coordinate pair. Returned as
// HTML so the tooltip can break onto two lines - plain-string
// tooltips can't wrap.
function formatWaypointTooltip(name, id, lat, lon) {
    const title = name || (id ? id.substring(0, 8) : 'Waypoint');
    const coords = latLonDms(lat, lon, ', ');
    return `<div class="wp-tooltip-name">${esc(title)}</div>` +
           `<div class="wp-tooltip-coords">${esc(coords)}</div>`;
}

function formatCreatedAt(iso) {
    if (!iso) return '—';
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return '—';
    const days = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
    const pad = (n) => String(n).padStart(2, '0');
    return `${days[d.getDay()]} ${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} `
        + `${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

function buildWaypointPopupHtml(id, name, lat, lon, createdAtIso) {
    const safeName = esc(name || id.substring(0, 8));
    // Coords mirror the hover-tooltip format (5dp ~ 1 m, hemisphere
    // letters) so hover-then-tap doesn't show two conflicting
    // renderings of the same position. Tap-only users (phones, iPad)
    // need the coords here because they never trigger hover.
    const coords = latLonDms(lat, lon, ', ');
    const created = formatCreatedAt(createdAtIso);
    // Mirror of the note popup so the helm gets the same affordance
    // grid (Go / Edit / Share / Delete) on either resource type.
    // Marker icon stays distinct (circle vs folded-page) so the
    // at-a-glance "navigable target vs annotation" cue survives.
    //
    // No "Focus" button: helm field-feedback was that tapping the
    // marker already centred enough of the map for the popup to
    // show the surrounding chart, so a separate Focus button was
    // visual noise. The layers-panel still has Focus as an explicit
    // affordance; the popup keeps the four actions a helm actually
    // wants on a single tap.
    return `
        <div class="note-popup-inner waypoint-popup-inner">
            <div class="note-popup-title">${safeName}</div>
            <div class="note-popup-meta">
                <div><span class="note-popup-meta-label">Coords:</span> <code>${esc(coords)}</code></div>
                <div><span class="note-popup-meta-label">Created:</span> ${esc(created)}</div>
            </div>
            <div class="note-popup-actions">
                <button class="waypoint-go-btn map-btn" type="button"
                        title="Navigate to this waypoint">Go</button>
                <button class="waypoint-edit-btn map-btn" type="button"
                        title="Edit name + description">Edit</button>
                <button class="waypoint-share-btn map-btn" type="button"
                        title="Share this waypoint via system share or copy to clipboard">Share</button>
                <button class="waypoint-delete-btn note-delete-btn" type="button">Delete</button>
            </div>
        </div>`;
}

/** Single-click button wiring - same shape as noteLayer's
 *  wireSimpleClick. Inlined here rather than promoted to popupHelpers
 *  because both note and waypoint layers will gain different
 *  destination methods and a shared helper would force a "method
 *  name + id" tuple convention that's not actually shared yet. */
function wireSimpleClick(popup, selector, dotNetMethod, id) {
    const el = popup.getElement();
    if (!el) return;
    const btn = el.querySelector(selector);
    if (!btn || btn._wired) return;
    btn._wired = true;
    btn.addEventListener('click', () => {
        const dotNetRef = getDotNetRef();
        if (dotNetRef) dotNetRef.invokeMethodAsync(dotNetMethod, id).catch(() => {});
    });
}

// MOB icons. Reused across every MOB waypoint (small fixed set):
//   _mobActiveIcon  - pulsing red, the active alarm
//   _mobInactiveIcon - solid red, a cleared MOB (persistent history)
// The .mob-icon + .mob-pulse classes carry the CSS animation; the
// inactive variant adds a class that suppresses the pulse keyframe
// but keeps the colour.
let _mobActiveIcon = null;
let _mobInactiveIcon = null;

function getMobIcon(isActive) {
    if (isActive) {
        return _mobActiveIcon ??= L.divIcon({
            className: 'mob-icon',
            html: '<div class="mob-pulse"></div>',
            iconSize: [20, 20],
            iconAnchor: [10, 10],
        });
    }
    return _mobInactiveIcon ??= L.divIcon({
        className: 'mob-icon mob-icon-inactive',
        html: '<div class="mob-pulse mob-pulse-inactive"></div>',
        iconSize: [20, 20],
        iconAnchor: [10, 10],
    });
}

// Active-MOB overlays. Keyed by waypoint id so a future multi-MOB
// scenario (two casualties, helm raises both) doesn't cross-wire
// circles + lines. Today the helm tap-rate + the two-tap arm pattern
// keeps this at zero or one entry, but the map shape is forward-
// compatible. Each entry holds:
//   { lat, lon, createdAt: Date, circle, line, midLabel, pinLabel }
const _activeMobOverlays = new Map();

// Latest own-boat position. Pushed from the leafletInterop mux on
// every updatePosition tick so the boat<->MOB line + label can
// refresh without the module reaching back into mux state. Cached
// here so a freshly-raised MOB picks up the most recent fix on
// first render even when the boat isn't moving.
let _selfLat = 0, _selfLon = 0;

// T+ counter ticks every 10 s. Single shared interval, started on
// first active-MOB add and stopped on last remove. 10 s gives a
// near-live readout without flooding the redraw loop; helm field-
// feedback was that 30 s felt sluggish during the active rescue
// window.
let _mobTickHandle = null;

function ensureMobTick() {
    if (_mobTickHandle != null) return;
    _mobTickHandle = setInterval(refreshAllMobLabels, 10_000);
}

function stopMobTickIfIdle() {
    if (_mobTickHandle == null) return;
    if (_activeMobOverlays.size > 0) return;
    clearInterval(_mobTickHandle);
    _mobTickHandle = null;
}

function refreshAllMobLabels() {
    for (const [id, ov] of _activeMobOverlays) {
        if (ov.pinLabel) ov.pinLabel.setContent(buildMobPinLabelHtml(ov));
    }
}

function pad2(n) { return String(n).padStart(2, '0'); }

function formatElapsed(createdAt) {
    if (!createdAt) return '';
    return mobElapsed((Date.now() - createdAt.getTime()) / 1000);
}

function buildMobPinLabelHtml(ov) {
    const { lat, lon, createdAt } = ov;
    const hh = pad2(createdAt.getHours());
    const mm = pad2(createdAt.getMinutes());
    const ss = pad2(createdAt.getSeconds());
    const elapsed = formatElapsed(createdAt);
    return `<strong>MOB ${hh}:${mm}:${ss}</strong> <span class="mob-elapsed">${elapsed}</span><br>` +
           latLonDms(lat, lon);
}

// Rich MOB popup: time + T+ + position + own-MMSI + GO + Share. No
// Edit / Delete buttons - MOB waypoints are immutable per the safety
// contract (cleared MOBs flip isActive=false but stay on the chart;
// edit would strip the MOB metadata; both refusals are also enforced
// at the C# JSInvokable trust boundary).
function buildMobPopupHtml(ov) {
    const { lat, lon, createdAt } = ov;
    const hh = pad2(createdAt.getHours());
    const mm = pad2(createdAt.getMinutes());
    const ss = pad2(createdAt.getSeconds());
    const elapsed = formatElapsed(createdAt);
    const mmsi = getOwnMmsi ? getOwnMmsi() : null;
    const mmsiRow = mmsi
        ? `<tr><td>MMSI</td><td>${esc(String(mmsi))}</td></tr>`
        : '';
    return `
        <div class="mob-popup-body">
            <div class="mob-popup-title">MOB <span class="mob-popup-elapsed">${elapsed}</span></div>
            <table class="mob-popup-table">
                <tr><td>Time</td><td>${hh}:${mm}:${ss}</td></tr>
                <tr><td>Lat</td><td>${esc(latDms(lat))}</td></tr>
                <tr><td>Lon</td><td>${esc(lonDms(lon))}</td></tr>
                ${mmsiRow}
            </table>
            <div class="mob-popup-actions">
                <button class="mob-popup-go-btn" type="button" data-ona-mob-go="1">GO</button>
                <button class="mob-popup-share-btn" type="button" data-ona-mob-share="1">Share</button>
            </div>
        </div>`;
}

function wireMobPopupActions(id, ov) {
    return (ev) => {
        const el = ev.popup.getElement();
        if (!el) return;
        const goBtn = el.querySelector('[data-ona-mob-go]');
        const shareBtn = el.querySelector('[data-ona-mob-share]');
        if (goBtn && !goBtn._wired) {
            goBtn._wired = true;
            goBtn.addEventListener('click', () => {
                if (!mapRef) return;
                // GO = pan + zoom-in to the casualty so the helm
                // sees the marker centred. setView lets us bump
                // the zoom (clamped) without hijacking the helm's
                // manual zoom when no MOB is up. Distinct from
                // the regular waypoint Go (which fires the autopilot
                // navigate-to JSInvokable); for a casualty we want
                // the helm to drive manually, not have the autopilot
                // take over.
                const targetZoom = Math.max(mapRef.getZoom(), 15);
                mapRef.setView([ov.lat, ov.lon], targetZoom);
            });
        }
        if (shareBtn && !shareBtn._wired) {
            shareBtn._wired = true;
            shareBtn.addEventListener('click', () => {
                const ref = getDotNetRef ? getDotNetRef() : null;
                if (!ref) return;
                ref.invokeMethodAsync('WaypointShare', id).catch(() => {});
            });
        }
    };
}

function parseCreatedAt(iso) {
    if (iso) {
        const d = new Date(iso);
        if (!Number.isNaN(d.getTime())) return d;
    }
    return new Date();
}

function buildActiveMobOverlays(id, lat, lon, createdAtIso, marker) {
    const overlay = {
        lat, lon,
        createdAt: parseCreatedAt(createdAtIso),
        circle: null,
        line: null,
        midLabel: null,
        pinLabel: null,
        marker,
    };
    // Alarm-radius circle: 50 m red ring around the casualty. Visual
    // anchor for "the helm should manoeuvre back to within this
    // radius"; matches the legacy mobLayer geometry.
    overlay.circle = L.circle([lat, lon], {
        radius: 50, color: colors.mob, fillColor: colors.mob,
        fillOpacity: 0.15, weight: 2,
    }).addTo(mapRef);
    // Dashed boat -> casualty line. Re-anchored on every boat fix
    // via setBoatPosition below. No-op until the first fix lands.
    overlay.line = L.polyline([[_selfLat, _selfLon], [lat, lon]], {
        color: colors.mob, weight: 2, dashArray: '4,4',
    }).addTo(mapRef);
    // Midpoint label: starts as plain "MOB" and switches to live
    // bearing / distance once a boat fix arrives. Used during the
    // return manoeuvre so the helm sees the closing geometry while
    // looking at the chart instead of a separate compass / range
    // readout.
    overlay.midLabel = L.tooltip({
        permanent: true, direction: 'center', className: 'mob-tooltip',
    })
        .setLatLng([(_selfLat + lat) / 2, (_selfLon + lon) / 2])
        .setContent('MOB')
        .addTo(mapRef);
    // At-pin label: time-of-drop + T+ elapsed + lat / lon. Helm-
    // feedback wanted this pinned to the chart (not in a toast at
    // the corner) so they can read the casualty fix straight off
    // the map while talking on the VHF mic. T+ ticks live via the
    // shared 10 s interval below.
    overlay.pinLabel = L.tooltip({
        permanent: true, direction: 'right', offset: [12, 0],
        className: 'mob-point-label',
    })
        .setLatLng([lat, lon])
        .setContent(buildMobPinLabelHtml(overlay))
        .addTo(mapRef);
    // Bind the rich theme-styled popup. Function form so each open
    // re-renders the T+ elapsed counter against the live clock
    // instead of baking a stale snapshot at marker-creation time.
    marker.bindPopup(() => buildMobPopupHtml(overlay), {
        className: 'mob-popup',
        maxWidth: 280,
        autoPan: true,
        autoPanPadding: [24, 24],
        keepInView: true,
    });
    marker.on('popupopen', wireMobPopupActions(id, overlay));
    _activeMobOverlays.set(id, overlay);
    ensureMobTick();
    // If we already have a boat fix cached (raised mid-session),
    // run one immediate refresh so the line + midpoint label show
    // the closing geometry without waiting for the next tick.
    if (_selfLat !== 0 || _selfLon !== 0) {
        refreshMobGeometry(overlay);
    }
}

function refreshMobGeometry(ov) {
    const dist = haversineMeters(_selfLat, _selfLon, ov.lat, ov.lon) * NM_PER_METER;
    const brg = bearingDeg(_selfLat, _selfLon, ov.lat, ov.lon);
    if (ov.line) ov.line.setLatLngs([[_selfLat, _selfLon], [ov.lat, ov.lon]]);
    if (ov.midLabel) {
        ov.midLabel.setLatLng([(_selfLat + ov.lat) / 2, (_selfLon + ov.lon) / 2]);
        ov.midLabel.setContent(`${brg.toFixed(0)}&deg; / ${dist.toFixed(2)} nm`);
    }
}

function tearDownMobOverlays(id) {
    const ov = _activeMobOverlays.get(id);
    if (!ov) return;
    if (mapRef) {
        if (ov.circle) mapRef.removeLayer(ov.circle);
        if (ov.line) mapRef.removeLayer(ov.line);
        if (ov.midLabel) mapRef.removeLayer(ov.midLabel);
        if (ov.pinLabel) mapRef.removeLayer(ov.pinLabel);
    }
    _activeMobOverlays.delete(id);
    stopMobTickIfIdle();
}

// Mux pushes the latest own-boat position. While any active MOB
// overlay is on the chart, the dashed line + midpoint bearing /
// distance label re-anchor on the boat end so the helm sees their
// progress back to the casualty live. No-op when no active MOB is
// on the chart - cheap to call per tick.
export function setBoatPosition(lat, lon) {
    _selfLat = lat;
    _selfLon = lon;
    if (_activeMobOverlays.size === 0) return;
    for (const ov of _activeMobOverlays.values()) {
        refreshMobGeometry(ov);
    }
}

export function addWaypointMarker(id, lat, lon, name, createdAtIso, isMob, isActive) {
    if (!mapRef || waypointMarkers.has(id)) return;
    // MOB waypoints render with the pulsing red icon (active) or the
    // solid red icon (cleared - persistent history). Non-MOB
    // waypoints keep the regular dot. The MOB icon is interactive on
    // the icon itself (no separate hit buffer) because the divIcon's
    // visible square already meets the 44 px touch target; for the
    // regular waypoint we keep the 6 px dot + 22 px invisible buffer
    // pattern.
    if (isMob) {
        const marker = L.marker([lat, lon], {
            icon: getMobIcon(!!isActive),
            zIndexOffset: 2000,    // above other waypoints + AIS markers
        });
        // Hover tooltip is the same shape as a regular waypoint
        // (name + coords) so the at-a-glance read is consistent
        // when the helm hovers a MOB pin.
        marker.bindTooltip(formatWaypointTooltip(name, id, lat, lon), {
            permanent: false, direction: 'right', offset: [10, 0],
            className: 'bearing-tooltip',
        });
        if (isActive) {
            // Active MOB: rich overlays + custom popup with GO /
            // Share / MMSI (no Edit, no Delete - safety contract).
            buildActiveMobOverlays(id, lat, lon, createdAtIso, marker);
        } else {
            // Cleared MOB: solid red icon + bare popup. Edit / Delete
            // refused at the C# JSInvokable trust boundary, but we
            // also hide the buttons here so the helm doesn't see an
            // affordance they can't use. Show GO + Share so the helm
            // can revisit a past casualty position.
            wireWaypointInteractions(marker, id, lat, lon, name, createdAtIso, /*isMob*/ true);
        }
        marker.addTo(mapRef);
        waypointMarkers.set(id, marker);
        return;
    }
    const marker = L.circleMarker([lat, lon], {
        radius: 6, color: colors.waypoint, fillColor: colors.waypoint, fillOpacity: 1, weight: 2,
        interactive: false,
    });
    // Wider invisible hit-buffer so a finger-wide tap registers.
    // 6 px visible radius = 12 px target; bumped to 22 px here gives
    // a 44 px hit (iPad WCAG floor). Visual marker stays 6 px so the
    // chart doesn't look cluttered.
    const hit = L.circleMarker([lat, lon], {
        radius: 22, opacity: 0, fillOpacity: 0, weight: 0, interactive: true
    });
    wireWaypointInteractions(hit, id, lat, lon, name, createdAtIso, /*isMob*/ false);
    // Group + add-to-map so remove/clear takes both layers down
    // together. MarkerLayer.remove -> map.removeLayer(group) which
    // removes its children.
    const group = L.layerGroup([marker, hit]).addTo(mapRef);
    waypointMarkers.set(id, group);
}

/**
 * Bind tooltip + popup + click handler to a non-active-MOB waypoint
 * tap target. For regular waypoints the target is the invisible
 * 22 px hit buffer (visible marker stays non-interactive); for a
 * cleared MOB the divIcon marker itself receives the events.
 *
 * When isMob is true, the popup omits the Edit + Delete buttons
 * (the safety contract refuses both at the C# trust boundary; the
 * UI hides them so the helm doesn't see an affordance they can't
 * use). Active MOBs use buildActiveMobOverlays + buildMobPopupHtml
 * directly and don't go through this path.
 */
function wireWaypointInteractions(target, id, lat, lon, name, createdAtIso, isMob) {
    target.bindTooltip(formatWaypointTooltip(name, id, lat, lon), {
        permanent: false, direction: 'right', offset: [10, 0],
        className: 'bearing-tooltip'
    });
    if (isMob) {
        // Cleared-MOB popup: name + coords + Go / Share (no Edit /
        // Delete). Reuses the regular waypoint popup CSS classes so
        // the visual is consistent.
        const safeName = esc(name || id.substring(0, 8));
        const coords = latLonDms(lat, lon, ', ');
        const created = formatCreatedAt(createdAtIso);
        target.bindPopup(`
            <div class="note-popup-inner waypoint-popup-inner">
                <div class="note-popup-title">${safeName}</div>
                <div class="note-popup-meta">
                    <div><span class="note-popup-meta-label">Coords:</span> <code>${esc(coords)}</code></div>
                    <div><span class="note-popup-meta-label">Created:</span> ${esc(created)}</div>
                    <div><span class="note-popup-meta-label">Status:</span> <em>cleared MOB</em></div>
                </div>
                <div class="note-popup-actions">
                    <button class="waypoint-go-btn map-btn" type="button"
                            title="Navigate to this waypoint">Go</button>
                    <button class="waypoint-share-btn map-btn" type="button"
                            title="Share this waypoint via system share or copy to clipboard">Share</button>
                </div>
            </div>`, {
            className: 'note-popup',
            maxWidth: 320,
            autoClose: true,
            closeButton: false,
        });
    } else {
        target.bindPopup(buildWaypointPopupHtml(id, name, lat, lon, createdAtIso), {
            className: 'note-popup',
            maxWidth: 320,
            autoClose: true,
            closeButton: false,
        });
    }
    target.on('click', (ev) => {
        // During edit modes, swallow the click and forward the
        // waypoint's location to whatever the user is plotting -
        // matches the note marker's edit-mode behaviour.
        const flags = getEditModeFlags();
        if (flags.routeEdit || flags.polygonEdit || flags.measure) {
            L.DomEvent.stopPropagation(ev);
            const ll = ev.latlng || target.getLatLng();
            // A click ON a marker is unambiguous: the helm tapped a
            // specific waypoint. Edit-mode dispatch follows the
            // historical priority (route -> polygon -> measure); the
            // measure-first override only applies to empty-map clicks
            // (see leafletInterop.js::map.on('click')) where intent
            // is "I clicked a free spot, what mode am I in?".
            if (flags.routeEdit)         editModeAddPoint('route', ll.lat, ll.lng);
            else if (flags.polygonEdit)  editModeAddPoint('polygon', ll.lat, ll.lng);
            else                         editModeAddPoint('measure', ll.lat, ll.lng);
            target.closePopup();
        }
    });
    target.on('popupopen', (ev) => {
        // No .waypoint-focus-btn binding - the button was removed
        // per helm field-feedback (see buildPopupHtml). The
        // C# WaypointFocus JSInvokable stays for the layers-panel
        // path; it just isn't wired from the popup any more.
        wireSimpleClick(ev.popup, '.waypoint-go-btn', 'WaypointGoTo', id);
        wireSimpleClick(ev.popup, '.waypoint-share-btn', 'WaypointShare', id);
        if (!isMob) {
            // Edit + Delete only on non-MOB waypoints. The C#
            // JSInvokables also refuse on isMob, so this is purely
            // a "don't show an unusable affordance" cue.
            wireSimpleClick(ev.popup, '.waypoint-edit-btn', 'WaypointEdit', id);
            wireDeleteConfirm(ev.popup, '.waypoint-delete-btn', 'DeleteWaypoint', id, getDotNetRef);
        }
    });
}

export function removeWaypointMarker(id) {
    if (!mapRef) return;
    // If this id had active-MOB overlays, tear them down too.
    // Idempotent + cheap when the id wasn't an active MOB.
    tearDownMobOverlays(id);
    waypointMarkers.remove(id);
}

export function dispose() {
    if (_mobTickHandle != null) { clearInterval(_mobTickHandle); _mobTickHandle = null; }
    _activeMobOverlays.clear();
    _selfLat = 0; _selfLon = 0;
    waypointMarkers.clear();
    mapRef = null;
    colors = null;
    getDotNetRef = null;
    getEditModeFlags = null;
    editModeAddPoint = null;
    getOwnMmsi = null;
}
