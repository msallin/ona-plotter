// Region (polygon / circle) overlay. Rendered as translucent filled
// polygons with a stronger border. Colour is muted lavender-gray that
// doesn't collide with routes (amber), waypoints (terracotta), notes
// (slate-blue) or the AIS palette.
//
// Includes the live "circle preview" drawn while the Add-Region
// dialog is open in Circle mode. Shares the region amber so the user
// sees the final shape at real size before committing. Replaced on
// every radius tap.

import { MarkerLayer } from './markerLayer.js';
import { esc, wireDeleteConfirm } from './popupHelpers.js';

const regionLayers = new MarkerLayer();
let mapRef = null;
let colors = null;
let getDotNetRef = null;
let getEditModeFlags = null;
let editModeAddPoint = null;

let circlePreviewLayer = null;

export function init(map, deps) {
    mapRef = map;
    colors = deps.colors;
    getDotNetRef = deps.getDotNetRef;
    getEditModeFlags = deps.getEditModeFlags;
    editModeAddPoint = deps.editModeAddPoint;
    regionLayers.setMap(map);
}

function buildRegionPopupHtml(_id, title, description) {
    const safeTitle = esc(title || '(untitled region)');
    const safeDesc = description ? esc(description).replace(/\n/g, '<br/>') : '';
    return `
        <div class="region-popup-inner">
            <div class="region-popup-title">${safeTitle}</div>
            ${safeDesc ? `<div class="region-popup-body">${safeDesc}</div>` : ''}
            <button class="region-delete-btn" type="button">Delete</button>
        </div>`;
}

// rings: [[[lat, lon], ...], ...]  -- one or more outer rings.
// A MultiPolygon region passes multiple rings; most regions are a
// single Polygon, so `rings` is a one-element array.
export function addRegion(id, rings, title, description) {
    if (!mapRef || regionLayers.has(id)) return;
    if (!Array.isArray(rings) || rings.length === 0) return;
    const group = L.layerGroup();
    const popupHtml = buildRegionPopupHtml(id, title, description);
    for (const ring of rings) {
        const poly = L.polygon(ring, {
            color: colors.region,
            fillColor: colors.region,
            fillOpacity: 0.18,
            weight: 1.8,
            opacity: 0.85,
        });
        poly.bindPopup(popupHtml, { className: 'region-popup', maxWidth: 280 });
        // Route / polygon / measure edit: clicks on regions append
        // to the in-progress shape instead of opening the region popup.
        poly.on('click', (ev) => {
            const flags = getEditModeFlags();
            if (flags.routeEdit || flags.polygonEdit || flags.measure) {
                L.DomEvent.stopPropagation(ev);
                const ll = ev.latlng;
                if (!ll) return;
                if (flags.routeEdit)         editModeAddPoint('route', ll.lat, ll.lng);
                else if (flags.polygonEdit)  editModeAddPoint('polygon', ll.lat, ll.lng);
                else                         editModeAddPoint('measure', ll.lat, ll.lng);
                poly.closePopup();
            }
        });
        poly.on('popupopen', (ev) => wireDeleteConfirm(ev.popup, '.region-delete-btn', 'DeleteRegion', id, getDotNetRef));
        group.addLayer(poly);
    }
    group.addTo(mapRef);
    regionLayers.set(id, group);
}

export function removeRegion(id) { regionLayers.remove(id); }
export function clearRegions() { regionLayers.clear(); }

// Pan to a region and open its popup. Accepts the first ring and
// uses its bounds so we frame whatever the user clicked in the
// Layers panel.
export function focusRegion(id, firstRing) {
    const layer = regionLayers.get(id);
    if (!layer || !mapRef) return;
    if (Array.isArray(firstRing) && firstRing.length > 0) {
        const bounds = L.latLngBounds(firstRing);
        mapRef.fitBounds(bounds, { padding: [40, 40], maxZoom: 14 });
    }
    // Open popup on the first polygon in the group.
    layer.eachLayer(l => { if (l.openPopup) l.openPopup(); return false; });
}

// ---- Region circle preview ----------------------------------------

export function setCirclePreview(lat, lon, radiusMeters) {
    if (!mapRef) return;
    if (circlePreviewLayer) {
        circlePreviewLayer.setLatLng([lat, lon]);
        circlePreviewLayer.setRadius(radiusMeters);
        return;
    }
    circlePreviewLayer = L.circle([lat, lon], {
        radius: radiusMeters,
        color: colors.region,
        fillColor: colors.region,
        fillOpacity: 0.12,
        weight: 1.6,
        dashArray: '4,4',
        interactive: false,
    }).addTo(mapRef);
}

export function clearCirclePreview() {
    if (circlePreviewLayer && mapRef) {
        mapRef.removeLayer(circlePreviewLayer);
        circlePreviewLayer = null;
    }
}

export function dispose() {
    regionLayers.clear();
    if (circlePreviewLayer && mapRef) {
        try { mapRef.removeLayer(circlePreviewLayer); } catch (_) { /* already gone */ }
    }
    circlePreviewLayer = null;
    mapRef = null;
    colors = null;
    getDotNetRef = null;
    getEditModeFlags = null;
    editModeAddPoint = null;
}
