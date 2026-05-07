// Region (polygon / circle) overlay. Rendered as translucent filled
// polygons with a stronger border. Decorative regions use the muted
// amber that doesn't collide with routes (orange), waypoints
// (terracotta), notes (slate-blue), or the AIS palette. Hazard
// regions render in red with a warning glyph at the centroid so the
// helm reads "stay out of this area" pre-attentively.
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

// Hazard stroke / fill. Hard-coded rather than reading a theme token
// because the alarm-rule palette (--sev-danger) uses a similar red
// and we want the region's stroke to read as "danger zone" without
// flickering under theme changes. The 0.20 fill alpha keeps the
// underlying chart readable at typical zoom; matches the decorative
// region's 0.18 to stay visually consistent.
const HAZARD_STROKE = '#dc2626';
const HAZARD_FILL = 'rgba(220, 38, 38, 0.20)';

function buildRegionPopupHtml(_id, title, description, opts) {
    const safeTitle = esc(title || '(untitled region)');
    const safeDesc = description ? esc(description).replace(/\n/g, '<br/>') : '';
    const isHazard = opts && opts.isHazard;
    const created = opts && opts.createdAt ? formatCreatedAt(opts.createdAt) : null;
    const area = opts && typeof opts.areaSqM === 'number' && opts.areaSqM > 0
        ? formatArea(opts.areaSqM) : null;
    const coords = opts ? formatCoords(opts) : null;
    const meta = [];
    if (isHazard) {
        meta.push('<div class="region-popup-meta region-popup-hazard">' +
                  '<span aria-hidden="true">&#9888;</span> Hazard area</div>');
    }
    if (area) {
        meta.push(`<div class="region-popup-meta"><span class="region-popup-k">Area:</span> ${esc(area)}</div>`);
    }
    if (coords) {
        meta.push(`<div class="region-popup-meta"><span class="region-popup-k">Coords:</span> ${esc(coords)}</div>`);
    }
    if (created) {
        meta.push(`<div class="region-popup-meta"><span class="region-popup-k">Created:</span> ${esc(created)}</div>`);
    }
    return `
        <div class="region-popup-inner ${isHazard ? 'region-popup-hazard-bg' : ''}">
            <div class="region-popup-title">${safeTitle}</div>
            ${safeDesc ? `<div class="region-popup-body">${safeDesc}</div>` : ''}
            ${meta.join('')}
            <div class="region-popup-actions">
                <button class="region-edit-btn" type="button">Edit</button>
                <button class="region-delete-btn" type="button">Delete</button>
            </div>
        </div>`;
}

// Format helpers: locale-free so the popup matches the rest of the
// helm-facing numbers (CultureInfo.InvariantCulture on the C# side).

function formatArea(sqm) {
    // < 1 ha: square metres. Otherwise hectares with a single decimal,
    // and km^2 above 100 ha so the helm reads "Area: 2.4 km^2" instead
    // of "240 ha".
    if (sqm < 10000) return Math.round(sqm).toLocaleString('en-GB') + ' m²';
    if (sqm < 1_000_000) return (sqm / 10_000).toFixed(1) + ' ha';
    return (sqm / 1_000_000).toFixed(2) + ' km²';
}

function formatCoords(o) {
    // Circle: "47.40000, 8.55000  r 250 m". Polygon: centroid +
    // vertex count, computed by the caller (passed through opts).
    if (typeof o.centerLat === 'number' && typeof o.centerLon === 'number'
        && typeof o.radiusMeters === 'number') {
        return `${o.centerLat.toFixed(5)}, ${o.centerLon.toFixed(5)}  r ${Math.round(o.radiusMeters)} m`;
    }
    if (typeof o.centroidLat === 'number' && typeof o.centroidLon === 'number') {
        const v = o.vertexCount ? `, ${o.vertexCount} vertices` : '';
        return `${o.centroidLat.toFixed(5)}, ${o.centroidLon.toFixed(5)}${v}`;
    }
    return null;
}

function formatCreatedAt(iso) {
    // ISO-8601 UTC -> "2026-04-01 14:32 UTC". Avoids locale ambiguity
    // ("4/1/2026" reads differently in en-GB vs en-US); the trailing
    // "UTC" warns the helm to add their own offset if they care.
    const d = new Date(iso);
    if (isNaN(d.getTime())) return null;
    const pad = (n) => String(n).padStart(2, '0');
    return `${d.getUTCFullYear()}-${pad(d.getUTCMonth() + 1)}-${pad(d.getUTCDate())} `
         + `${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())} UTC`;
}

// Centroid of a [[lat, lon], ...] ring via the area-weighted
// shoelace centroid formula. For sailing-scale regions the planar
// approximation is fine; the popup just needs a "where is this
// thing?" tag, not a survey-grade point.
function ringCentroid(ring) {
    if (!Array.isArray(ring) || ring.length < 3) return null;
    let twiceArea = 0, cx = 0, cy = 0;
    const n = ring.length;
    for (let i = 0; i < n; i++) {
        const [y1, x1] = ring[i];
        const [y2, x2] = ring[(i + 1) % n];
        const cross = (x1 * y2) - (x2 * y1);
        twiceArea += cross;
        cx += (x1 + x2) * cross;
        cy += (y1 + y2) * cross;
    }
    if (twiceArea === 0) {
        // Degenerate ring (collinear points). Fall back to mean of
        // the vertices so the popup still has a position to anchor.
        let sx = 0, sy = 0;
        for (const [y, x] of ring) { sx += x; sy += y; }
        return [sy / n, sx / n];
    }
    const sixA = 3 * twiceArea;
    return [cy / sixA, cx / sixA];
}

export function init(map, deps) {
    mapRef = map;
    colors = deps.colors;
    getDotNetRef = deps.getDotNetRef;
    getEditModeFlags = deps.getEditModeFlags;
    editModeAddPoint = deps.editModeAddPoint;
    regionLayers.setMap(map);
}

// rings: [[[lat, lon], ...], ...]  - one or more outer rings.
// A MultiPolygon region passes multiple rings; most regions are a
// single Polygon, so `rings` is a one-element array.
export function addRegion(id, rings, title, description,
                          isHazard, areaSqM,
                          centerLat, centerLon, radiusMeters,
                          createdAtIso) {
    if (!mapRef || regionLayers.has(id)) return;
    if (!Array.isArray(rings) || rings.length === 0) return;
    const group = L.layerGroup();
    // Centroid: prefer circle centre when available (exact); else
    // compute from the first ring.
    const haveCircle = typeof centerLat === 'number' && typeof centerLon === 'number';
    const centroid = haveCircle
        ? [centerLat, centerLon]
        : ringCentroid(rings[0]);
    const popupOpts = {
        isHazard: !!isHazard,
        areaSqM: areaSqM,
        centerLat: haveCircle ? centerLat : null,
        centerLon: haveCircle ? centerLon : null,
        radiusMeters: haveCircle ? radiusMeters : null,
        centroidLat: centroid ? centroid[0] : null,
        centroidLon: centroid ? centroid[1] : null,
        vertexCount: rings[0].length,
        createdAt: createdAtIso,
    };
    const popupHtml = buildRegionPopupHtml(id, title, description, popupOpts);
    const stroke = isHazard ? HAZARD_STROKE : colors.region;
    const fill = isHazard ? HAZARD_FILL : colors.region;
    for (const ring of rings) {
        const poly = L.polygon(ring, {
            color: stroke,
            fillColor: fill,
            fillOpacity: isHazard ? 1.0 : 0.18,   // HAZARD_FILL already carries alpha
            weight: isHazard ? 2.4 : 1.8,
            opacity: 0.92,
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
        poly.on('popupopen', (ev) => {
            wireDeleteConfirm(ev.popup, '.region-delete-btn', 'DeleteRegion', id, getDotNetRef);
            wireEditButton(ev.popup, id);
        });
        group.addLayer(poly);
    }
    // Always-visible warning glyph for hazard regions: a small
    // amber-on-red triangle anchored at the centroid (circle centre
    // when available; polygon centroid otherwise). Rendered as a
    // Leaflet DivIcon so it picks up CSS theming and stays sharp at
    // every zoom; non-interactive so a click on the glyph doesn't
    // intercept popup-open.
    if (isHazard && centroid) {
        const warn = L.marker(centroid, {
            icon: L.divIcon({
                className: 'region-hazard-glyph',
                html: '<span aria-hidden="true">&#9888;</span>',
                iconSize: [24, 24],
                iconAnchor: [12, 12],
            }),
            interactive: false,
            keyboard: false,
            zIndexOffset: 200,
        });
        group.addLayer(warn);
    }
    group.addTo(mapRef);
    regionLayers.set(id, group);
}

// Wire the popup's Edit button to call back into C# with the region id.
// Same pattern as wireDeleteConfirm but no confirm step - the helm
// taps Edit, the polygon-edit panel opens populated with the region's
// vertices, and Save commits the changes (or Cancel discards).
function wireEditButton(popup, id) {
    const root = popup.getElement && popup.getElement();
    if (!root) return;
    const btn = root.querySelector('.region-edit-btn');
    if (!btn) return;
    btn.addEventListener('click', (e) => {
        e.preventDefault();
        e.stopPropagation();
        popup.close && popup.close();
        const ref = typeof getDotNetRef === 'function' ? getDotNetRef() : null;
        if (ref) ref.invokeMethodAsync('EditRegion', id);
    }, { once: true });
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
