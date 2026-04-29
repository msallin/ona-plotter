// Standalone waypoint markers (SignalK /resources/waypoints).
// Rendered as a small filled circle with a wider invisible
// hit-buffer so a finger-wide tap registers (44 px iPad WCAG floor)
// while the visible marker stays 6 px to keep the chart legible.
// Hover shows a name + coordinates tooltip; click opens a popup
// with name + Delete (two-step confirm).

import { MarkerLayer } from './markerLayer.js';
import { esc, wireDeleteConfirm } from './popupHelpers.js';

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
// HTML so the tooltip can break onto two lines -- plain-string
// tooltips can't wrap.
function formatWaypointTooltip(name, id, lat, lon) {
    const title = name || (id ? id.substring(0, 8) : 'Waypoint');
    const ns = lat >= 0 ? 'N' : 'S';
    const ew = lon >= 0 ? 'E' : 'W';
    const coords = `${Math.abs(lat).toFixed(5)}° ${ns}, ${Math.abs(lon).toFixed(5)}° ${ew}`;
    return `<div class="wp-tooltip-name">${esc(title)}</div>` +
           `<div class="wp-tooltip-coords">${esc(coords)}</div>`;
}

function buildWaypointPopupHtml(id, name, lat, lon) {
    const safeName = esc(name || id.substring(0, 8));
    // Coords mirror the hover-tooltip format (5dp ~ 1 m, hemisphere
    // letters) so hover-then-tap doesn't show two conflicting
    // renderings of the same position. Tap-only users (phones, iPad)
    // need the coords here because they never trigger hover.
    const ns = lat >= 0 ? 'N' : 'S';
    const ew = lon >= 0 ? 'E' : 'W';
    const coords = `${Math.abs(lat).toFixed(5)}° ${ns}, ${Math.abs(lon).toFixed(5)}° ${ew}`;
    return `
        <div class="note-popup-inner">
            <div class="note-popup-title">${safeName}</div>
            <div class="note-popup-coords">${esc(coords)}</div>
            <button class="waypoint-delete-btn note-delete-btn" type="button">Delete</button>
        </div>`;
}

export function addWaypointMarker(id, lat, lon, name) {
    if (!mapRef || waypointMarkers.has(id)) return;
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
    // Events fire on the hit buffer; the visible marker is non-
    // interactive so the two don't double-handle.
    hit.bindTooltip(formatWaypointTooltip(name, id, lat, lon), {
        permanent: false, direction: 'right', offset: [10, 0],
        className: 'bearing-tooltip'
    });
    hit.bindPopup(buildWaypointPopupHtml(id, name, lat, lon), {
        className: 'note-popup',
        maxWidth: 280,
        autoClose: true,
        closeButton: false,
    });
    hit.on('click', (ev) => {
        // During edit modes, swallow the click and forward the
        // waypoint's location to whatever the user is plotting --
        // matches the note marker's edit-mode behaviour.
        const flags = getEditModeFlags();
        if (flags.routeEdit || flags.polygonEdit || flags.measure) {
            L.DomEvent.stopPropagation(ev);
            const ll = ev.latlng || marker.getLatLng();
            if (flags.routeEdit)         editModeAddPoint('route', ll.lat, ll.lng);
            else if (flags.polygonEdit)  editModeAddPoint('polygon', ll.lat, ll.lng);
            else                         editModeAddPoint('measure', ll.lat, ll.lng);
            hit.closePopup();
        }
    });
    hit.on('popupopen', (ev) => wireDeleteConfirm(ev.popup, '.waypoint-delete-btn', 'DeleteWaypoint', id, getDotNetRef));
    // Group + add-to-map so remove/clear takes both layers down
    // together. MarkerLayer.remove -> map.removeLayer(group) which
    // removes its children.
    const group = L.layerGroup([marker, hit]).addTo(mapRef);
    waypointMarkers.set(id, group);
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
