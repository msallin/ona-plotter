// Standalone waypoint markers (SignalK /resources/waypoints).
// Rendered as a small filled circle with a wider invisible
// hit-buffer so a finger-wide tap registers (44 px iPad WCAG floor)
// while the visible marker stays 6 px to keep the chart legible.
// Hover shows a name + coordinates tooltip; click opens a popup
// with name + Delete (two-step confirm).

import { MarkerLayer } from './markerLayer.js';
import { esc, wireDeleteConfirm } from './popupHelpers.js';
import { latLonDms } from './format.js';

const waypointMarkers = new MarkerLayer();
let mapRef = null;
let colors = null;
let getDotNetRef = null;
let getEditModeFlags = null;
let editModeAddPoint = null;

export function init(map, deps) {
    mapRef = map;
    colors = deps.colors;
    getDotNetRef = deps.getDotNetRef;
    getEditModeFlags = deps.getEditModeFlags;
    editModeAddPoint = deps.editModeAddPoint;
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
        wireWaypointInteractions(marker, id, lat, lon, name, createdAtIso, true, isActive);
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
    wireWaypointInteractions(hit, id, lat, lon, name, createdAtIso, false, false);
    // Group + add-to-map so remove/clear takes both layers down
    // together. MarkerLayer.remove -> map.removeLayer(group) which
    // removes its children.
    const group = L.layerGroup([marker, hit]).addTo(mapRef);
    waypointMarkers.set(id, group);
}

/**
 * Bind tooltip + popup + click handler to a waypoint's tap target.
 * For regular waypoints the target is the invisible 22 px hit
 * buffer (visible marker stays non-interactive); for MOB the
 * divIcon marker itself receives the events (its 20 px square
 * already meets the touch-target floor and adding a separate hit
 * circle on top of the pulsing icon would intercept the visual).
 */
function wireWaypointInteractions(target, id, lat, lon, name, createdAtIso, isMob, isActive) {
    // Tooltip on hover (quick identification); popup on click (full
    // name + Delete). Same pattern as notes/regions so the tap-to-act
    // affordance is consistent across user-placed objects.
    //
    // Coordinates accompany the name: F5 precision gives ~1 m
    // resolution which is what a helm reading coords off a chart
    // actually needs, without pretending to a decimal of longitude
    // that GPS jitter already eats. Hemisphere letters (N/S, E/W)
    // keep the reading unambiguous when the waypoint is near the
    // equator or the prime meridian.
    target.bindTooltip(formatWaypointTooltip(name, id, lat, lon), {
        permanent: false, direction: 'right', offset: [10, 0],
        className: 'bearing-tooltip'
    });
    target.bindPopup(buildWaypointPopupHtml(id, name, lat, lon, createdAtIso), {
        className: 'note-popup',
        maxWidth: 320,
        autoClose: true,
        closeButton: false,
    });
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
        wireSimpleClick(ev.popup, '.waypoint-edit-btn', 'WaypointEdit', id);
        wireSimpleClick(ev.popup, '.waypoint-share-btn', 'WaypointShare', id);
        wireDeleteConfirm(ev.popup, '.waypoint-delete-btn', 'DeleteWaypoint', id, getDotNetRef);
    });
}

export function removeWaypointMarker(id) {
    if (!mapRef) return;
    waypointMarkers.remove(id);
}

export function dispose() {
    waypointMarkers.clear();
    mapRef = null;
    colors = null;
    getDotNetRef = null;
    getEditModeFlags = null;
    editModeAddPoint = null;
}
