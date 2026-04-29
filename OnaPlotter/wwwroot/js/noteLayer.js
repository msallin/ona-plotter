// Note markers: geolocated text annotations (SignalK
// /resources/notes). Rendered as a small folded-page pin that reads
// distinct from waypoints (circular) and routes (amber line). Click
// opens a popup with title + description and a Delete button (two-
// step confirm) that round-trips to C# via the cached dotNetRef.
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

function buildNotePopupHtml(_id, title, description) {
    const safeTitle = esc(title || '(untitled)');
    const safeDesc = description ? esc(description).replace(/\n/g, '<br/>') : '';
    return `
        <div class="note-popup-inner">
            <div class="note-popup-title">${safeTitle}</div>
            ${safeDesc ? `<div class="note-popup-body">${safeDesc}</div>` : ''}
            <button class="note-delete-btn" type="button">Delete</button>
        </div>`;
}

export function addNoteMarker(id, lat, lon, title, description) {
    if (!mapRef || noteMarkers.has(id)) return;
    const marker = L.marker([lat, lon], { icon: getNoteIcon() }).addTo(mapRef);
    marker.bindPopup(buildNotePopupHtml(id, title, description), {
        className: 'note-popup',
        maxWidth: 280,
        autoClose: true,
    });
    // During edit modes, swallow the click and append to whatever
    // the user is building. Same guard as AIS markers.
    marker.on('click', (ev) => {
        const flags = getEditModeFlags();
        if (flags.routeEdit || flags.polygonEdit || flags.measure) {
            L.DomEvent.stopPropagation(ev);
            const ll = ev.latlng || marker.getLatLng();
            if (flags.routeEdit)         editModeAddPoint('route', ll.lat, ll.lng);
            else if (flags.polygonEdit)  editModeAddPoint('polygon', ll.lat, ll.lng);
            else                         editModeAddPoint('measure', ll.lat, ll.lng);
            marker.closePopup();
        }
    });
    // Wire up the delete button when the popup opens. We query within
    // the popup DOM so an id collision with something else on the
    // page can't hijack the click.
    marker.on('popupopen', (ev) => wireDeleteConfirm(ev.popup, '.note-delete-btn', 'DeleteNote', id, getDotNetRef));
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
