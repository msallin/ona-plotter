// Drag feedback shared by the edit layers (route, polygon, measure):
// while a marker is dragged, a ghost stays at its original position
// with a dashed line to the cursor and a tooltip showing how far it
// has moved, so the helm always sees "how far from where it was".

import { haversineMeters, NM_PER_METER } from './geoMath.js';

export function formatDelta(meters) {
    return meters < 1000
        ? `Δ ${meters.toFixed(0)} m`
        : `Δ ${(meters * NM_PER_METER).toFixed(2)} nm`;
}

/**
 * @param marker  draggable L.marker
 * @param opts
 *   container    () => L.LayerGroup | L.Map the ghost lives in (read at
 *                each event so a torn-down layer is a no-op)
 *   color        () => stroke colour of the delta line
 *   icon         { className, html, size } of the ghost marker
 *   tooltipOffset  y offset that clears the draggable icon's top edge
 *   onDrag(ll)   per-drag work; return false to skip the ghost update
 *   onEnd()      optional, after the ghost is removed
 */
export function attachDragGhost(marker, opts) {
    let ghostMarker = null;
    let ghostLine = null;
    let origLL = null;

    marker.on('dragstart', (e) => {
        const container = opts.container();
        const { className, html, size } = opts.icon;
        origLL = e.target.getLatLng();
        ghostMarker = L.marker(origLL, {
            icon: L.divIcon({
                className, html,
                iconSize: [size, size],
                iconAnchor: [size / 2, size / 2]
            }),
            interactive: false, keyboard: false, zIndexOffset: 500
        }).addTo(container);
        ghostLine = L.polyline([origLL, origLL], {
            color: opts.color(), weight: 1.5, opacity: 0.7, dashArray: '3,4',
            interactive: false
        }).addTo(container);
        // Bound once; setTooltipContent per drag event is cheaper than
        // rebinding at ~60 Hz. Positioned explicitly via setLatLng, so
        // no `sticky`. direction:'top' keeps the label above the
        // finger so the marker doesn't hide its own delta.
        ghostLine.bindTooltip('Δ 0 m', {
            permanent: true,
            direction: 'top',
            offset: [0, opts.tooltipOffset],
            className: 'measure-tooltip'
        }).openTooltip(origLL);
    });

    marker.on('drag', (e) => {
        const ll = e.target.getLatLng();
        if (opts.onDrag(ll) === false) return;
        if (ghostLine && origLL) {
            ghostLine.setLatLngs([origLL, ll]);
            ghostLine.setTooltipContent(formatDelta(haversineMeters(origLL.lat, origLL.lng, ll.lat, ll.lng)));
            const tt = ghostLine.getTooltip();
            if (tt) tt.setLatLng(ll);
        }
    });

    marker.on('dragend', () => {
        const container = opts.container();
        if (container) {
            if (ghostMarker) container.removeLayer(ghostMarker);
            if (ghostLine) container.removeLayer(ghostLine);
        }
        ghostMarker = null; ghostLine = null; origLL = null;
        opts.onEnd?.();
    });
}
