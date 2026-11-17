// Saved-route overlay: a solid polyline per route with a wide
// invisible hit line for finger taps, vertex dots, and a popup
// carrying Activate / Edit / Delete. The active route's own overlay
// lives in activeRouteLayer.js.

import { MarkerLayer } from './markerLayer.js';
import { esc, wireClick, wireDeleteConfirm } from './popupHelpers.js';
import * as measureLayerMod from './measureLayer.js';
import { addEditWaypoint, isActive as routeEditIsActive } from './routeEditLayer.js';
import { addPolygonVertexInternal, isActive as polygonEditIsActive } from './polygonEditLayer.js';

const routeLayers = new MarkerLayer();  // keyed by route ID
let mapRef = null;
let colors = null;
let getDotNetRef = null;

export function init(map, deps) {
    mapRef = map;
    colors = deps.colors;
    getDotNetRef = deps.getDotNetRef;
    routeLayers.setMap(map);
}

export function dispose() {
    routeLayers.clear();
    mapRef = null;
    colors = null;
    getDotNetRef = null;
}

// Route polyline colour lives in colors.route (read from
// --ann-route at init). Call-sites use colors.route directly so
// a palette edit propagates without a module reload.
//
// Line-style convention used across the chartplotter:
//   - solid  = "planned" (a saved route, or future legs of an active route)
//   - dashed = "ship-to-waypoint" (bearing line, current-leg overlay)
//   - dotted = "already passed" (legs of the active route the boat crossed)
// Keep this consistent when adding new route-like overlays.

// Add a route as a polyline. coords is [[lat, lon], ...].
//
// Clicking the line opens a popup with Activate + Delete so the helm
// doesn't have to burrow into the Layers panel to do the two most
// common actions on a saved route. During route-edit / polygon-edit /
// measure the click is absorbed as a new waypoint instead (same guard
// as AIS markers) so the user can't accidentally fire Activate/Delete
// while trying to extend a route.
export function addRoute(id, name, coords, totalNm) {
    if (!mapRef || routeLayers.has(id)) return;
    const line = L.polyline(coords, {
        color: colors.route, weight: 2.5, opacity: 0.8
    }).addTo(mapRef);

    // Invisible wider polyline acts as the tap hitbox. The visible
    // route is only 2.5 px wide, which is a miserable target on a
    // touch screen (users end up dropping waypoints on the map while
    // trying to activate a route). Same pattern used by the
    // route-edit flow (routeEditLayer.js) for live-edit vertices; do it on all
    // saved routes too so the Activate / Edit / Delete popup is
    // actually reachable from the polyline.
    const hitLine = L.polyline(coords, {
        color: colors.route, weight: 36, opacity: 0, interactive: true
    }).addTo(mapRef);

    // totalNm is precomputed in C# (see Map.razor's AddRouteToMap
    // call site - RouteProgress.TotalDistanceMeters / 1852); JS does
    // not redo the haversine sum, per the project rule (decisions/math
    // in C#). Defensive
    // fallback if a future caller forgets the arg: 0 reads as
    // "missing" and the popup just shows "N WP" without the distance,
    // which is honest rather than wrong.
    const nmTotal = (typeof totalNm === 'number' && isFinite(totalNm)) ? totalNm : 0;
    const popupOptions = { className: 'route-popup', maxWidth: 320, autoClose: true };
    const popupHtml = () => buildRoutePopupHtml(id, name, coords.length, nmTotal);
    line.bindPopup(popupHtml(), popupOptions);
    hitLine.bindPopup(popupHtml(), popupOptions);

    const onLineClick = (ev, sourceLine) => {
        if (routeEditIsActive() || polygonEditIsActive() || measureLayerMod.isActive()) {
            L.DomEvent.stopPropagation(ev);
            const ll = ev.latlng;
            if (!ll) return;
            // Measure beats route / polygon edit on a route-polyline
            // click. Mirrors the same priority on the map-level click
            // handler in leafletInterop.js; otherwise measuring AROUND an existing
            // route is impossible because the polyline eats every
            // click. Marker / region / active-route polyline clicks
            // deliberately use route-first order - those targets are
            // unambiguous (the helm tapped a specific item) so the
            // measure-first override is scoped to the empty-map and
            // stale-route polyline cases only.
            if (measureLayerMod.isActive())            measureLayerMod.addMeasurePoint(ll.lat, ll.lng);
            else if (routeEditIsActive())     addEditWaypoint(ll.lat, ll.lng);
            else if (polygonEditIsActive())   addPolygonVertexInternal(ll.lat, ll.lng);
            sourceLine.closePopup();
        }
    };
    line.on('click', (ev) => onLineClick(ev, line));
    hitLine.on('click', (ev) => onLineClick(ev, hitLine));
    const wirePopup = (ev) => {
        // Single-tap Activate / Edit, no confirm: neither is destructive.
        // Edit closes the popup first (the edit toolbar takes over the
        // viewport); Activate closes it once the server has the course.
        wireClick(ev.popup, '.route-activate-btn', getDotNetRef, 'ActivateRouteById', [id], { close: 'after' });
        wireClick(ev.popup, '.route-edit-btn', getDotNetRef, 'EditRouteById', [id], { close: 'before' });
        wireDeleteConfirm(ev.popup, '.route-delete-btn', 'DeleteRouteById', id, getDotNetRef);
    };
    line.on('popupopen', wirePopup);
    hitLine.on('popupopen', wirePopup);

    // Waypoint dots at each coordinate.
    // bindTooltip writes its string argument to innerHTML, so the
    // server-supplied route name has to be HTML-escaped or a name
    // like '<img src=x onerror=...>' would execute. esc() once outside
    // the loop - name doesn't change per vertex.
    const group = L.layerGroup([line, hitLine]).addTo(mapRef);
    const safeTipName = name ? esc(name) : '';
    for (let i = 0; i < coords.length; i++) {
        const dot = L.circleMarker(coords[i], {
            radius: 4, color: colors.route, fillColor: colors.route, fillOpacity: 1, weight: 1
        });
        dot.bindTooltip(safeTipName ? `${safeTipName} [${i + 1}]` : `WPT ${i + 1}`, { className: 'bearing-tooltip' });
        dot.addTo(group);
    }
    routeLayers.set(id, group);
}

function buildRoutePopupHtml(id, name, wpCount, nmTotal) {
    const safeName = esc(name || `Route ${id.substring(0, 6)}`);
    return `
        <div class="route-popup-body">
            <div class="route-popup-title">${safeName}</div>
            <div class="route-popup-meta">${wpCount} WP &middot; ${nmTotal.toFixed(1)} nm</div>
            <div class="route-popup-actions">
                <button class="route-activate-btn" type="button">Activate</button>
                <button class="route-edit-btn" type="button">Edit</button>
                <button class="route-delete-btn" type="button">Delete</button>
            </div>
        </div>`;
}

export function removeRoute(id) { routeLayers.remove(id); }

// Updates the geometry + popup metadata of an existing route layer
// in place. Mirrors REST PUT semantics on /resources/routes/<id>:
// same id, possibly different waypoint count + total nm + name.
//
// Falls through to addRoute when the id isn't yet known (upsert
// behaviour) so callers don't need to branch on whether this is the
// first delta the helm has seen for this id.
//
// Why in-place vs remove+add: a remove+add cycle has a flicker
// window where the polyline disappears, and forces the helm to
// re-locate the dashed-leg overlay (active-route layer) which
// hooks into the route's coords. setLatLngs is the leaflet-native
// "the geometry changed; keep everything else" path - click
// handlers + popupopen wiring + the layer's identity in the
// activeRouteLayer's diff stay valid. Vertex DOTS are the one
// piece that has to rebuild because their count is geometry-
// dependent (route-edit can add or remove waypoints).
export function updateRoute(id, name, coords, totalNm) {
    if (!mapRef) return;
    const group = routeLayers.get(id);
    if (!group) {
        // Upsert: first time we hear about this route. Defer to addRoute
        // which builds the full layer stack from scratch.
        return addRoute(id, name, coords, totalNm);
    }

    const layers = group.getLayers();
    if (layers.length < 2) {
        // Defensive: layer group somehow lost its line + hitLine. Rebuild.
        routeLayers.remove(id);
        return addRoute(id, name, coords, totalNm);
    }

    // Layer order from addRoute(): [line, hitLine, ...vertex_dots].
    const [line, hitLine, ...dots] = layers;

    // Update visible polyline + invisible touch hitbox geometry.
    line.setLatLngs(coords);
    hitLine.setLatLngs(coords);

    // Refresh popup labels (waypoint count + total nm + name may all
    // have changed). buildRoutePopupHtml is referenced by addRoute as
    // well; same call site keeps the two paths in lockstep.
    const nmTotal = (typeof totalNm === 'number' && isFinite(totalNm)) ? totalNm : 0;
    const html = buildRoutePopupHtml(id, name, coords.length, nmTotal);
    line.setPopupContent(html);
    hitLine.setPopupContent(html);

    // Vertex dots: count is geometry-dependent (route-edit adds /
    // removes waypoints), so rebuild rather than try to setLatLng
    // each surviving one. Escape the route name once - bindTooltip
    // writes its argument to innerHTML, so a remote-edited name must
    // not be interpolated raw.
    for (const d of dots) group.removeLayer(d);
    const safeTipName = name ? esc(name) : '';
    for (let i = 0; i < coords.length; i++) {
        const dot = L.circleMarker(coords[i], {
            radius: 4, color: colors.route, fillColor: colors.route, fillOpacity: 1, weight: 1
        });
        dot.bindTooltip(safeTipName ? `${safeTipName} [${i + 1}]` : `WPT ${i + 1}`, { className: 'bearing-tooltip' });
        dot.addTo(group);
    }
}
