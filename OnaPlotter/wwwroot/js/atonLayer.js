// AIS Type 21 marks: cardinal/lateral/special buoys, beacons,
// lighthouses, racons. Render as L.divIcon so we don't have to ship
// 16 SVG files; the icon-builder draws inline SVG sized to the marker
// and tinted by symbol kind. Virtual AtoNs (no physical mark in the
// water - e.g. wreck warnings) get a dashed outline so the helm
// doesn't go looking for an actual buoy.
//
// AtoNs are static enough that the C# side pushes the full set on
// store-change rather than per-update. setAtons replaces the entire
// marker layer; ids that go away in the new payload are removed.

import { MarkerLayer } from './markerLayer.js';

// IALA Region A palette. Cardinal marks use yellow + black bands;
// lateral red = port (Region A), green = starboard. Other marks
// (isolated danger, safe water, special) carry their own palette.
const ATON_COLOR_PORT = '#d62828';      // red lateral (Region A)
const ATON_COLOR_STBD = '#06a13a';      // green lateral (Region A)
const ATON_COLOR_CARDINAL_Y = '#f4c430'; // amber-yellow
const ATON_COLOR_CARDINAL_K = '#1b1b1b'; // near-black
const ATON_COLOR_DANGER = '#1b1b1b';    // isolated danger (black with red bands)
const ATON_COLOR_DANGER_BAND = '#d62828';
const ATON_COLOR_SAFE = '#d62828';      // safe water (red+white vertical stripes)
const ATON_COLOR_SPECIAL = '#f4c430';   // yellow with X topmark
const ATON_COLOR_BASE = '#3b82f6';      // base station (blue square)
const ATON_COLOR_UNKNOWN = '#6b7280';

const atonMarkers = new MarkerLayer();
let mapRef = null;
let atonsVisible = true;

export function init(map) {
    mapRef = map;
    atonMarkers.setMap(map);
}

// Build a 28x28 SVG markup string for the given (symbol, side) pair.
// Coordinates assume (14, 14) is the centre; the L.divIcon iconAnchor
// places the centre on the lat/lon. Virtual marks ride a dashed
// stroke; real marks get a solid stroke.
function buildAtonSvg(symbol, side, isVirtual) {
    const stroke = isVirtual ? '4 2' : '0';
    const strokeWidth = 1.5;
    if (symbol === 'Cardinal') {
        // Two stacked black/yellow cones; orientation by cardinal side
        // (north = double-up, south = double-down, east = up+down,
        // west = down+up). Width 14, height 24 centred at (14,14).
        const yTop = side === 'North' || side === 'East'
            ? ATON_COLOR_CARDINAL_K : ATON_COLOR_CARDINAL_Y;
        const yBot = side === 'South' || side === 'East'
            ? ATON_COLOR_CARDINAL_K : ATON_COLOR_CARDINAL_Y;
        return `<svg xmlns="http://www.w3.org/2000/svg" width="28" height="28" viewBox="0 0 28 28">
            <rect x="9" y="3" width="10" height="10" fill="${yTop}" stroke="#000" stroke-width="${strokeWidth}" stroke-dasharray="${stroke}" />
            <rect x="9" y="13" width="10" height="10" fill="${yBot}" stroke="#000" stroke-width="${strokeWidth}" stroke-dasharray="${stroke}" />
            <text x="14" y="18" text-anchor="middle" font-size="11" font-weight="bold" fill="#fff" font-family="sans-serif">${side[0]}</text>
        </svg>`;
    }
    if (symbol === 'Lateral') {
        // Region A: port=red can, starboard=green cone.
        const fill = side === 'Port' ? ATON_COLOR_PORT : ATON_COLOR_STBD;
        const shape = side === 'Port'
            // Can shape (rectangle with flat top)
            ? `<rect x="6" y="6" width="16" height="16" fill="${fill}" stroke="#000" stroke-width="${strokeWidth}" stroke-dasharray="${stroke}" />`
            // Cone shape (triangle pointing up)
            : `<polygon points="14,4 22,22 6,22" fill="${fill}" stroke="#000" stroke-width="${strokeWidth}" stroke-dasharray="${stroke}" />`;
        return `<svg xmlns="http://www.w3.org/2000/svg" width="28" height="28" viewBox="0 0 28 28">${shape}</svg>`;
    }
    if (symbol === 'IsolatedDanger') {
        // Black sphere with a red horizontal band, two black topmark
        // balls. Simplified to a circle for legibility at marker size.
        return `<svg xmlns="http://www.w3.org/2000/svg" width="28" height="28" viewBox="0 0 28 28">
            <circle cx="14" cy="14" r="9" fill="${ATON_COLOR_DANGER}" stroke="#000" stroke-width="${strokeWidth}" stroke-dasharray="${stroke}" />
            <rect x="5" y="11" width="18" height="6" fill="${ATON_COLOR_DANGER_BAND}" />
            <text x="14" y="18" text-anchor="middle" font-size="11" font-weight="bold" fill="#fff" font-family="sans-serif">!</text>
        </svg>`;
    }
    if (symbol === 'SafeWater') {
        // Red and white vertical stripes, single sphere topmark.
        return `<svg xmlns="http://www.w3.org/2000/svg" width="28" height="28" viewBox="0 0 28 28">
            <circle cx="14" cy="14" r="9" fill="#fff" stroke="#000" stroke-width="${strokeWidth}" stroke-dasharray="${stroke}" />
            <path d="M14 5 L14 23" stroke="${ATON_COLOR_SAFE}" stroke-width="6" />
        </svg>`;
    }
    if (symbol === 'Special') {
        // Yellow X-mark.
        return `<svg xmlns="http://www.w3.org/2000/svg" width="28" height="28" viewBox="0 0 28 28">
            <circle cx="14" cy="14" r="9" fill="${ATON_COLOR_SPECIAL}" stroke="#000" stroke-width="${strokeWidth}" stroke-dasharray="${stroke}" />
            <path d="M9 9 L19 19 M19 9 L9 19" stroke="#000" stroke-width="2" />
        </svg>`;
    }
    if (symbol === 'BaseStation') {
        // Antenna icon: square plus radiating lines. Reuses the AtoN
        // marker layer because shore.basestations.* arrives on the
        // same delta path.
        return `<svg xmlns="http://www.w3.org/2000/svg" width="28" height="28" viewBox="0 0 28 28">
            <rect x="6" y="14" width="16" height="10" fill="${ATON_COLOR_BASE}" stroke="#000" stroke-width="${strokeWidth}" />
            <path d="M14 14 L14 4 M10 7 L18 7 M11 4 L17 4" stroke="${ATON_COLOR_BASE}" stroke-width="2" fill="none" />
        </svg>`;
    }
    // Unknown / unmapped: small grey diamond so the helm sees that
    // SOMETHING is there even when the type code didn't match.
    return `<svg xmlns="http://www.w3.org/2000/svg" width="28" height="28" viewBox="0 0 28 28">
        <polygon points="14,5 23,14 14,23 5,14" fill="${ATON_COLOR_UNKNOWN}" stroke="#000" stroke-width="${strokeWidth}" stroke-dasharray="${stroke}" />
    </svg>`;
}

function makeAtonIcon(symbol, side, isVirtual) {
    return L.divIcon({
        className: 'aton-marker',  // CSS hook for global styling
        html: buildAtonSvg(symbol, side, isVirtual),
        iconSize: [28, 28],
        iconAnchor: [14, 14],
    });
}

function buildAtonPopupHtml(a) {
    const titleParts = [];
    if (a.name) titleParts.push(a.name);
    else if (a.mmsi) titleParts.push(a.mmsi);
    else titleParts.push('AtoN');
    if (a.virtual) titleParts.push('(virtual)');
    const title = titleParts.join(' ');
    const subtitle = a.typeName || (a.typeId != null ? `Type ${a.typeId}` : '');
    return `<div class="aton-popup">
        <strong>${title}</strong>
        ${subtitle ? `<div class="aton-popup-sub">${subtitle}</div>` : ''}
        ${a.mmsi && a.name ? `<div class="aton-popup-mmsi">MMSI ${a.mmsi}</div>` : ''}
    </div>`;
}

/**
 * Replace the rendered AtoN set. Adds new ids, updates moved entries
 * (rare - AtoNs don't usually move), removes ids missing from the
 * payload. Caller (Map.razor) pushes the whole snapshot from
 * AtonStore on each OnAtonsUpdated event; the layer is small enough
 * (typical harbour 10-50 entries, big port maybe 200) that a full
 * rebuild on every change isn't a perf problem.
 *
 * @param {Array<{
 *   context: string, name?: string, mmsi?: string,
 *   lat: number, lon: number,
 *   typeId?: number, typeName?: string,
 *   symbol: string, side: string, virtual?: boolean
 * }>} atons
 */
export function setAtons(atons) {
    if (!mapRef) return;
    const seen = new Set();
    for (const a of atons) {
        if (a.lat == null || a.lon == null
            || !isFinite(a.lat) || !isFinite(a.lon)) continue;
        seen.add(a.context);
        const icon = makeAtonIcon(a.symbol, a.side, !!a.virtual);
        const existing = atonMarkers.get(a.context);
        if (existing) {
            existing.setLatLng([a.lat, a.lon]);
            existing.setIcon(icon);
            existing.setPopupContent(buildAtonPopupHtml(a));
        } else {
            // Honour the visibility flag on creation. Without this
            // guard a setAtons that runs while atonsVisible=false
            // would silently add fresh markers to the map - the user
            // hides the layer, a reconnect repopulates the store, and
            // the buoys reappear despite the toggle being off.
            const m = L.marker([a.lat, a.lon], { icon })
                .bindPopup(buildAtonPopupHtml(a), { autoPan: false });
            if (atonsVisible) m.addTo(mapRef);
            atonMarkers.set(a.context, m);
        }
    }
    // Remove ids no longer in the snapshot.
    for (const id of atonMarkers.keys()) {
        if (!seen.has(id)) atonMarkers.remove(id);
    }
}

/** Visibility toggle. Hides without losing the marker layer state so
 *  a re-show doesn't have to re-fetch. The layer remains registered
 *  with Leaflet - we just remove from / add to the map. */
export function setAtonsVisible(visible) {
    if (!mapRef) return;
    if (atonsVisible === !!visible) return;
    atonsVisible = !!visible;
    for (const id of atonMarkers.keys()) {
        const m = atonMarkers.get(id);
        if (!m) continue;
        if (atonsVisible) m.addTo(mapRef);
        else mapRef.removeLayer(m);
    }
}

export function dispose() {
    atonMarkers.clear();
    mapRef = null;
}
