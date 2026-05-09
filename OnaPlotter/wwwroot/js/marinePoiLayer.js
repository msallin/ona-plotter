// OSM marine-POI overlay. Render thin: classification + colour
// resolution happen in C# (Services/Pois/OverpassResponseParser.cs +
// MarinePoiCategory enum); this module receives the resolved
// {category, lat, lon, name, tags} payload and stamps a divIcon SVG
// per category. Adding a new category means: enum value (C#) + SVG
// branch (this file).

import { MarkerLayer } from './markerLayer.js';

// Sailor-distinct palette so categories are recognisable at a glance
// from across the cockpit. Avoid red (collides with port-lateral
// AtoNs and CPA chips) and amber (collides with guard-zone).
const COLOR_FUEL = '#0066cc';        // blue: marine fuel (universal)
const COLOR_MARINA = '#5a3fb8';      // purple: marina
const COLOR_HARBOUR = '#0a5b8c';     // dark blue: harbour
const COLOR_MOORING = '#2b8a3e';     // green: mooring
const COLOR_SLIPWAY = '#8a4f00';     // brown: slipway
const COLOR_PIER = '#555';           // grey: pier
const COLOR_CHANDLERY = '#b8417e';   // pink: chandlery
const COLOR_WATER = '#06b6d4';       // cyan: drinking water
const COLOR_PUMPOUT = '#777';        // dark grey: pump-out

const poiMarkers = new MarkerLayer();
let mapRef = null;
let visible = false;  // Default off. Categories are individually opt-in.

export function init(map) {
    mapRef = map;
    poiMarkers.setMap(map);
}

/**
 * Build a 28x28 SVG markup string for the given category. Coordinates
 * assume (14, 14) is the centre; the L.divIcon iconAnchor places the
 * centre on the lat/lon. Each category has a recognisable shape +
 * colour combination so a helm scanning the chart can read "fuel
 * dock" or "drinking water" without zooming the popup.
 */
function buildMarinePoiSvg(category) {
    const sw = 1.5;
    switch (category) {
        case 'Fuel':
            // Fuel pump silhouette (stylised square with a nozzle).
            return `<svg xmlns="http://www.w3.org/2000/svg" width="28" height="28" viewBox="0 0 28 28">
                <circle cx="14" cy="14" r="11" fill="${COLOR_FUEL}" stroke="#fff" stroke-width="${sw}" />
                <text x="14" y="19" text-anchor="middle" font-size="14" font-weight="bold" fill="#fff" font-family="sans-serif">F</text>
            </svg>`;
        case 'Marina':
            // Anchor symbol on the category colour.
            return `<svg xmlns="http://www.w3.org/2000/svg" width="28" height="28" viewBox="0 0 28 28">
                <circle cx="14" cy="14" r="11" fill="${COLOR_MARINA}" stroke="#fff" stroke-width="${sw}" />
                <text x="14" y="19" text-anchor="middle" font-size="14" font-weight="bold" fill="#fff" font-family="sans-serif">M</text>
            </svg>`;
        case 'Harbour':
            return `<svg xmlns="http://www.w3.org/2000/svg" width="28" height="28" viewBox="0 0 28 28">
                <circle cx="14" cy="14" r="11" fill="${COLOR_HARBOUR}" stroke="#fff" stroke-width="${sw}" />
                <text x="14" y="19" text-anchor="middle" font-size="14" font-weight="bold" fill="#fff" font-family="sans-serif">H</text>
            </svg>`;
        case 'Mooring':
            // Smaller mooring-ball shape.
            return `<svg xmlns="http://www.w3.org/2000/svg" width="22" height="22" viewBox="0 0 22 22">
                <circle cx="11" cy="11" r="8" fill="${COLOR_MOORING}" stroke="#fff" stroke-width="${sw}" />
                <line x1="11" y1="3" x2="11" y2="6" stroke="#fff" stroke-width="2" />
            </svg>`;
        case 'Slipway':
            // Triangle pointing into water.
            return `<svg xmlns="http://www.w3.org/2000/svg" width="26" height="26" viewBox="0 0 26 26">
                <polygon points="13,4 23,22 3,22" fill="${COLOR_SLIPWAY}" stroke="#fff" stroke-width="${sw}" />
                <text x="13" y="20" text-anchor="middle" font-size="9" font-weight="bold" fill="#fff" font-family="sans-serif">S</text>
            </svg>`;
        case 'Pier':
            // Rectangle silhouette.
            return `<svg xmlns="http://www.w3.org/2000/svg" width="26" height="26" viewBox="0 0 26 26">
                <rect x="4" y="9" width="18" height="8" fill="${COLOR_PIER}" stroke="#fff" stroke-width="${sw}" />
                <text x="13" y="16" text-anchor="middle" font-size="9" font-weight="bold" fill="#fff" font-family="sans-serif">P</text>
            </svg>`;
        case 'Chandlery':
            return `<svg xmlns="http://www.w3.org/2000/svg" width="26" height="26" viewBox="0 0 26 26">
                <circle cx="13" cy="13" r="10" fill="${COLOR_CHANDLERY}" stroke="#fff" stroke-width="${sw}" />
                <text x="13" y="17" text-anchor="middle" font-size="11" font-weight="bold" fill="#fff" font-family="sans-serif">C</text>
            </svg>`;
        case 'DrinkingWater':
            // Water-drop shape stylised as a circle with a ~ stroke.
            return `<svg xmlns="http://www.w3.org/2000/svg" width="22" height="22" viewBox="0 0 22 22">
                <circle cx="11" cy="11" r="8" fill="${COLOR_WATER}" stroke="#fff" stroke-width="${sw}" />
                <text x="11" y="15" text-anchor="middle" font-size="11" font-weight="bold" fill="#fff" font-family="sans-serif">W</text>
            </svg>`;
        case 'PumpOut':
            return `<svg xmlns="http://www.w3.org/2000/svg" width="22" height="22" viewBox="0 0 22 22">
                <circle cx="11" cy="11" r="8" fill="${COLOR_PUMPOUT}" stroke="#fff" stroke-width="${sw}" />
                <text x="11" y="15" text-anchor="middle" font-size="9" font-weight="bold" fill="#fff" font-family="sans-serif">PO</text>
            </svg>`;
        default:
            // Unknown: small grey diamond. The C# parser drops
            // un-classified elements before they reach here, so this
            // branch is a defence-in-depth no-show that won't fire in
            // practice.
            return `<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 20 20">
                <polygon points="10,2 18,10 10,18 2,10" fill="#888" stroke="#fff" stroke-width="${sw}" />
            </svg>`;
    }
}

// Cache divIcons by category. Categories are a small fixed set
// (fuel, marina, harbour, mooring, slipway, pier, chandlery,
// drinkingWater, pumpOut, ...), so the cache caps at ~10 entries.
// Without this, every setMarinePois push allocated a fresh divIcon
// per POI even though most shared the same category - on a 200-POI
// busy harbour render that's ~190 redundant SVG constructions per
// push. Identity-stable returns pair with the setIcon-skip-on-same
// guard below.
const _marinePoiIconCache = new Map();

function makeMarinePoiIcon(category) {
    let icon = _marinePoiIconCache.get(category);
    if (icon !== undefined) return icon;
    icon = L.divIcon({
        className: 'marine-poi-marker',
        html: buildMarinePoiSvg(category),
        iconSize: [28, 28],
        iconAnchor: [14, 14],
    });
    _marinePoiIconCache.set(category, icon);
    return icon;
}

function escapeHtml(s) {
    if (s == null) return '';
    return String(s)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#39;');
}

// Subset of OSM tags we surface as labelled rows in the popup. The
// C# parser already trimmed to a whitelist; this list picks the most
// helm-relevant first and renders them as "Label: value". Anything
// else from the projected tags falls into a "More" fold that keeps
// the popup compact.
const POPUP_TAG_LABELS = [
    ['operator', 'Operator'],
    ['opening_hours', 'Hours'],
    ['phone', 'Phone'],
    ['contact:phone', 'Phone'],
    ['vhf_channel', 'VHF'],
    ['fuel:diesel', 'Diesel'],
    ['fuel:petrol', 'Petrol'],
    ['capacity', 'Berths'],
    ['depth', 'Depth'],
    ['fee', 'Fee'],
];

function buildMarinePoiPopupHtml(p) {
    const title = p.name ? escapeHtml(p.name) : escapeHtml(p.category);
    const subtitle = p.name ? escapeHtml(p.category) : '';

    const rows = [];
    const tags = p.tags || {};
    const seen = new Set();
    for (const [key, label] of POPUP_TAG_LABELS) {
        if (key in tags && tags[key]) {
            rows.push(`<div class="marine-poi-popup-row"><span class="marine-poi-popup-label">${label}:</span> ${escapeHtml(tags[key])}</div>`);
            seen.add(key);
        }
    }

    // Website link (raw or contact:website). Render as a clickable
    // anchor; the helm taps to open in the system browser.
    const website = tags.website || tags['contact:website'];
    if (website) {
        const safeUrl = escapeHtml(website);
        rows.push(`<div class="marine-poi-popup-row"><a href="${safeUrl}" target="_blank" rel="noopener noreferrer">Website</a></div>`);
    }

    // OSM deeplink so a curious helm can see / edit the source.
    // p.id is "n123", "w456", "r789" — split into type and number.
    let osmLink = '';
    if (p.id && p.id.length > 1) {
        const typeChar = p.id[0];
        const num = p.id.substring(1);
        const typeWord = typeChar === 'n' ? 'node' : typeChar === 'w' ? 'way' : typeChar === 'r' ? 'relation' : null;
        if (typeWord) {
            osmLink = `<div class="marine-poi-popup-row marine-poi-popup-source"><a href="https://www.openstreetmap.org/${typeWord}/${escapeHtml(num)}" target="_blank" rel="noopener noreferrer">View on OSM</a></div>`;
        }
    }

    return `<div class="marine-poi-popup">
        <strong>${title}</strong>
        ${subtitle ? `<div class="marine-poi-popup-sub">${subtitle}</div>` : ''}
        ${rows.join('')}
        ${osmLink}
    </div>`;
}

/**
 * Replace the rendered marine-POI set. The controller pushes the
 * full bbox+category-filtered cache snapshot on every render, so we
 * diff by id: add new ids, update existing, drop ids no longer in
 * the payload. Same shape as the AtoN layer.
 *
 * @param {Array<{id: string, category: string, lat: number, lon: number, name?: string, tags?: object}>} pois
 */
export function setMarinePois(pois) {
    if (!mapRef) return;
    const seen = new Set();
    for (const p of pois) {
        if (p.lat == null || p.lon == null
            || !isFinite(p.lat) || !isFinite(p.lon)) continue;
        if (!p.id) continue;
        seen.add(p.id);
        const icon = makeMarinePoiIcon(p.category);
        const existing = poiMarkers.get(p.id);
        if (existing) {
            existing.setLatLng([p.lat, p.lon]);
            // Identity check: cache returns the same divIcon for the
            // same category, so a setIcon is only needed when the POI
            // actually changed category (rare). Skipping the no-op
            // setIcon avoids a DOM detach + re-attach per POI per push.
            if (existing._lastIcon !== icon) {
                existing.setIcon(icon);
                existing._lastIcon = icon;
            }
            existing.setPopupContent(buildMarinePoiPopupHtml(p));
        } else {
            const m = L.marker([p.lat, p.lon], { icon })
                .bindPopup(buildMarinePoiPopupHtml(p), { autoPan: false });
            m._lastIcon = icon;
            if (visible) m.addTo(mapRef);
            poiMarkers.set(p.id, m);
        }
    }
    // Remove ids no longer in the snapshot.
    for (const id of poiMarkers.keys()) {
        if (!seen.has(id)) poiMarkers.remove(id);
    }
}

/** Master visibility toggle. Hides without losing marker state so the
 *  controller can re-show without forcing a fetch. */
export function setMarinePoisVisible(v) {
    if (!mapRef) return;
    if (visible === !!v) return;
    visible = !!v;
    for (const id of poiMarkers.keys()) {
        const m = poiMarkers.get(id);
        if (!m) continue;
        if (visible) m.addTo(mapRef);
        else mapRef.removeLayer(m);
    }
}

export function dispose() {
    poiMarkers.clear();
    mapRef = null;
    visible = false;
}
