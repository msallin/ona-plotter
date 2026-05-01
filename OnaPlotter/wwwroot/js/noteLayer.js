// Note markers: geolocated text annotations (SignalK
// /resources/notes). Rendered as a small folded-page pin that reads
// distinct from waypoints (circular) and routes (amber line). Hover
// shows the title (so the helm can scan a chart full of pins without
// clicking each one), click opens a popup with title + description +
// coordinates + created-at + four action buttons (Go / Edit / Share /
// Delete). Each button round-trips to C# via the cached dotNetRef.
//
// Note pin colour: darker amber in the same user-annotation family
// as routes and waypoints. Shape (folded-page vs circle vs line)
// carries the "this is a note" signal, not hue. Note hue reads from
// MapColors.note via readMapColors() so a CSS palette tweak cascades;
// the dark stroke stays a literal because the legend doesn't
// reference it.

import { MarkerLayer } from './markerLayer.js';
import { esc, wireDeleteConfirm } from './popupHelpers.js';

const NOTE_STROKE = '#7a5418';

const noteMarkers = new MarkerLayer();
let mapRef = null;
let colors = null;
let getDotNetRef = null;
let getEditModeFlags = null;
let editModeAddPoint = null;
let noteIconCached = null;

export function init(map, deps) {
    mapRef = map;
    colors = deps.colors;
    getDotNetRef = deps.getDotNetRef;
    getEditModeFlags = deps.getEditModeFlags;
    editModeAddPoint = deps.editModeAddPoint;
    noteMarkers.setMap(map);
    // Reset cached icon so a fresh palette read at initMap re-tints
    // the SVG fill on next add.
    noteIconCached = null;
}

function makeNoteIcon() {
    // Modern sticky-note pin, 22x28. Rounded-corner card (no
    // skeuomorphic folded-corner), white text strokes for better
    // contrast against the amber fill, clean teardrop tail pointing
    // down to the map coord. Anchor is bottom-centre so the tip of
    // the tail lands on the target lat/lon. Softer drop-shadow than
    // the v1 icon so the pin lifts off the chart without adding
    // visual noise.
    const svg = `
        <svg width="22" height="28" viewBox="0 0 22 28" xmlns="http://www.w3.org/2000/svg"
             style="filter: drop-shadow(0 1.5px 2px rgba(0,0,0,0.35));">
            <rect x="2" y="2" width="18" height="18" rx="4" ry="4"
                  fill="${colors.note}" stroke="${NOTE_STROKE}" stroke-width="1.2"/>
            <line x1="6"  y1="8"  x2="16" y2="8"
                  stroke="rgba(255,255,255,0.92)" stroke-width="1.4" stroke-linecap="round"/>
            <line x1="6"  y1="12" x2="16" y2="12"
                  stroke="rgba(255,255,255,0.92)" stroke-width="1.4" stroke-linecap="round"/>
            <line x1="6"  y1="16" x2="12" y2="16"
                  stroke="rgba(255,255,255,0.92)" stroke-width="1.4" stroke-linecap="round"/>
            <path d="M8 20 Q11 20 11 26 Q11 20 14 20 Z"
                  fill="${colors.note}" stroke="${NOTE_STROKE}" stroke-width="1.2"
                  stroke-linejoin="round"/>
        </svg>`;
    return L.divIcon({
        className: 'note-icon',
        html: svg,
        iconSize: [22, 28],
        iconAnchor: [11, 28],
        popupAnchor: [0, -26],
    });
}

function getNoteIcon() {
    if (!noteIconCached) noteIconCached = makeNoteIcon();
    return noteIconCached;
}

/** Format a [lat, lon] pair as "47.40123°N, 8.50456°E" for the popup
 *  body. Hemisphere letters keep it readable for a helm who isn't
 *  used to signed decimal degrees; 5 fractional digits = ~1 m on
 *  any latitude, matching what GPS feeds typically resolve. */
function formatLatLon(lat, lon) {
    const ns = lat >= 0 ? 'N' : 'S';
    const ew = lon >= 0 ? 'E' : 'W';
    return `${Math.abs(lat).toFixed(5)}°${ns}, ${Math.abs(lon).toFixed(5)}°${ew}`;
}

/** Format an ISO-8601 timestamp string for the popup. Local-tz
 *  rendering because the helm is reading the helm clock; UTC would
 *  be a foreign reference. Empty / unparseable input -> dash so the
 *  caller doesn't need to guard. */
function formatCreatedAt(iso) {
    if (!iso) return '—';
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return '—';
    // ddd, yyyy-MM-dd HH:mm matches the trips-table style on the
    // History page so muscle memory carries.
    const days = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
    const pad = (n) => String(n).padStart(2, '0');
    return `${days[d.getDay()]} ${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} `
        + `${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

function buildNotePopupHtml(_id, title, description, lat, lon, createdAtIso) {
    const safeTitle = esc(title || '(untitled)');
    const safeDesc = description ? esc(description).replace(/\n/g, '<br/>') : '';
    const coords = formatLatLon(lat, lon);
    const created = formatCreatedAt(createdAtIso);
    return `
        <div class="note-popup-inner">
            <div class="note-popup-title">${safeTitle}</div>
            ${safeDesc ? `<div class="note-popup-body">${safeDesc}</div>` : ''}
            <div class="note-popup-meta">
                <div><span class="note-popup-meta-label">Coords:</span> <code>${esc(coords)}</code></div>
                <div><span class="note-popup-meta-label">Created:</span> ${esc(created)}</div>
            </div>
            <div class="note-popup-actions">
                <button class="note-go-btn map-btn" type="button"
                        title="Set this note's position as the navigation destination">Go</button>
                <button class="note-edit-btn map-btn" type="button"
                        title="Rename this note">Edit</button>
                <button class="note-share-btn map-btn" type="button"
                        title="Share this note via system share or copy to clipboard">Share</button>
                <button class="note-delete-btn map-btn" type="button">Delete</button>
            </div>
        </div>`;
}

/** Wire a single-click button in the popup to a [JSInvokable] method.
 *  Mirror of wireDeleteConfirm minus the two-step confirm: Go / Edit
 *  / Share aren't destructive enough to need it, and an extra tap on
 *  every action would wear out fast. _wired flag so re-opening the
 *  popup doesn't double-bind. */
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

export function addNoteMarker(id, lat, lon, title, description, createdAtIso) {
    if (!mapRef || noteMarkers.has(id)) return;
    const marker = L.marker([lat, lon], { icon: getNoteIcon() }).addTo(mapRef);
    // Hover tooltip showing the title. Helps the helm scan a chart
    // dotted with notes without clicking each pin. Empty title falls
    // through to "(untitled)" so the tooltip always carries something.
    marker.bindTooltip(title || '(untitled)', {
        direction: 'top',
        offset: [0, -22],   // matches popupAnchor so tooltip clears the icon
        opacity: 0.95,
    });
    marker.bindPopup(buildNotePopupHtml(id, title, description, lat, lon, createdAtIso), {
        className: 'note-popup',
        maxWidth: 320,
        autoClose: true,
    });
    // During edit modes, swallow the click and append to whatever
    // the user is building. Same guard as AIS markers.
    marker.on('click', (ev) => {
        const flags = getEditModeFlags();
        if (flags.routeEdit || flags.polygonEdit || flags.measure) {
            L.DomEvent.stopPropagation(ev);
            const ll = ev.latlng || marker.getLatLng();
            // Measure beats route / polygon edit -- helm asked for
            // priority parity across every layer's click handler so
            // a started measurement always wins.
            if (flags.measure)           editModeAddPoint('measure', ll.lat, ll.lng);
            else if (flags.routeEdit)    editModeAddPoint('route', ll.lat, ll.lng);
            else if (flags.polygonEdit)  editModeAddPoint('polygon', ll.lat, ll.lng);
            marker.closePopup();
        }
    });
    // Wire the four action buttons when the popup opens. Querying
    // INSIDE the popup DOM (not document-wide) means an id collision
    // with another element can't hijack the click.
    marker.on('popupopen', (ev) => {
        wireSimpleClick(ev.popup, '.note-go-btn', 'NoteGoTo', id);
        wireSimpleClick(ev.popup, '.note-edit-btn', 'NoteEdit', id);
        wireSimpleClick(ev.popup, '.note-share-btn', 'NoteShare', id);
        wireDeleteConfirm(ev.popup, '.note-delete-btn', 'DeleteNote', id, getDotNetRef);
    });
    noteMarkers.set(id, marker);
}

export function removeNoteMarker(id) { noteMarkers.remove(id); }
export function clearNotes() { noteMarkers.clear(); }

// Open the popup on a note marker if it's currently rendered. No-op
// when the id isn't present (note not yet loaded, or notes hidden).
export function openNotePopup(id) {
    const m = noteMarkers.get(id);
    if (m) m.openPopup();
}

export function dispose() {
    noteMarkers.clear();
    noteIconCached = null;
    mapRef = null;
    colors = null;
    getDotNetRef = null;
    getEditModeFlags = null;
    editModeAddPoint = null;
}
