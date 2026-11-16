/**
 * Course-up / head-up rotation for the chart map.
 *
 * The map container is rotated with a CSS transform (rotate(-deg) about
 * its centre). Leaflet itself knows nothing about the rotation, so every
 * place where it turns a screen position into a map position has to be
 * corrected here:
 *   * map.mouseEventToContainerPoint: taps, clicks, context menu, wheel
 *     and pinch pivots, canvas hit-testing (all route through it).
 *   * L.Draggable: map drag-pan and marker drags (anchor move handle,
 *     route / polygon vertices) compute the move from raw client deltas.
 *   * Tooltips and popups: counter-rotated about their anchor point so
 *     text stays upright and on the same side of its feature on screen.
 * The same transforms are exported for the custom long-press and
 * context-menu placement code in leafletInterop.js.
 *
 * Chosen over a rotate plugin (leaflet-rotate) because that plugin
 * replaces large parts of Leaflet's map / marker / renderer internals,
 * which the chart-upscale decorator, radar canvas and course-line layers
 * build on; three narrow patches here keep Leaflet's own code paths.
 * The patches read Leaflet 1.9 private fields (_startPoint, _startPos,
 * _newPos, _lastEvent, _containerLeft, _containerBottom); a Leaflet
 * upgrade has to re-check them.
 *
 * Coordinates: screen y points down, so a positive angle is clockwise,
 * matching CSS rotate(). The container is rotated by -deg, so a vector
 * on screen maps back to the container frame by rotating it by +deg.
 */

/** Wraps an angle delta into (-180, 180]. */
export function normalizeDeltaDeg(delta) {
    let d = ((delta % 360) + 360) % 360;
    if (d > 180) d -= 360;
    return d;
}

/**
 * Continuous rotation for a new target heading: the previous applied
 * angle plus the shortest delta, so a CSS transition between them never
 * takes the long way round.
 *
 * Example: current -359 (displayed as 1 deg), target 359 -> -361.
 */
export function unwrapRotation(currentDeg, targetDeg) {
    return currentDeg + normalizeDeltaDeg(targetDeg - currentDeg);
}

/** Rotates a screen vector clockwise by deg (y down). */
export function rotateVector(x, y, deg) {
    const r = deg * Math.PI / 180;
    const c = Math.cos(r), s = Math.sin(r);
    return { x: x * c - y * s, y: x * s + y * c };
}

/**
 * Screen client coordinates -> container point of a container rotated
 * by -deg about its centre. `rect` is the container's bounding client
 * rect (the axis-aligned box around the rotated element; its centre is
 * the rotation centre); width / height are the unrotated offset size.
 */
export function clientToContainerPoint(clientX, clientY, rect, width, height, deg,
                                       clientLeft = 0, clientTop = 0) {
    const cx = rect.left + rect.width / 2;
    const cy = rect.top + rect.height / 2;
    const v = rotateVector(clientX - cx, clientY - cy, deg);
    return { x: width / 2 + v.x - clientLeft, y: height / 2 + v.y - clientTop };
}

/**
 * Container point -> position on screen relative to the container's
 * unrotated layout box (what an absolutely positioned sibling such as
 * the context menu needs). Inverse of clientToContainerPoint.
 */
export function containerPointToLayoutPoint(x, y, width, height, deg) {
    const v = rotateVector(x - width / 2, y - height / 2, -deg);
    return { x: width / 2 + v.x, y: height / 2 + v.y };
}

// Rotation state of the one map that can rotate (the chart page).
let rotatedMap = null;
let rotationDeg = 0;
let installed = false;

function activeContainer() {
    return rotatedMap ? rotatedMap.getContainer() : null;
}

function belongsToRotatedMap(map) {
    return !!map && map === rotatedMap;
}

// Inline transform suffix that counter-rotates an overlay. Reads the
// angle from --map-rot on the container so a rotation change restyles
// every open tooltip / popup without touching each element.
const COUNTER_ROTATE = ' rotate(var(--map-rot, 0deg))';

function appendCounterRotation(el, origin) {
    el.style.transformOrigin = origin;
    if (!el.style.transform.endsWith(COUNTER_ROTATE)) {
        el.style.transform += COUNTER_ROTATE;
    }
}

/**
 * Patches Leaflet once. Inert for any map other than the one passed to
 * attachRotation, so the History map keeps stock behaviour.
 */
export function installRotationSupport(L) {
    if (installed) return;
    installed = true;

    // Drag (map pan and marker drags). Leaflet computes the new position
    // from the raw client delta divided by the parent's bounding-box
    // scale, both wrong inside a rotated container. 'predrag' fires
    // before the position is written; recompute it from the raw delta.
    L.Draggable.addInitHook(function () {
        this.on('predrag', function () {
            const container = activeContainer();
            if (!container || rotationDeg % 360 === 0 || !container.contains(this._element)) return;
            const e = this._lastEvent;
            const p = e && e.touches && e.touches.length === 1 ? e.touches[0] : e;
            if (!p || !this._startPoint || !this._startPos) return;
            const v = rotateVector(p.clientX - this._startPoint.x, p.clientY - this._startPoint.y, rotationDeg);
            this._newPos = this._startPos.add(L.point(v.x, v.y));
        });
    });

    // Tooltips: rotate about the feature point (the position Leaflet was
    // asked to place the tooltip at) so a "right" tooltip stays right of
    // its feature on screen. The direction classes add a margin that
    // shifts the border box the origin is measured from.
    const tooltipSetPosition = L.Tooltip.prototype._setPosition;
    L.Tooltip.prototype._setPosition = function (pos) {
        tooltipSetPosition.call(this, pos);
        const el = this._container;
        if (!belongsToRotatedMap(this._map) || !el) return;
        const placed = L.DomUtil.getPosition(el);
        const style = getComputedStyle(el);
        const x = pos.x - placed.x - (parseFloat(style.marginLeft) || 0);
        const y = pos.y - placed.y - (parseFloat(style.marginTop) || 0);
        appendCounterRotation(el, `${x}px ${y}px`);
    };

    // Popups: rotate about the anchored latlng. The popup is bottom-
    // anchored (style.bottom / style.left relative to its translate), so
    // the pivot's y is measured from the element's bottom edge, minus
    // the margin that lifts the bubble above its tip (Leaflet's CSS:
    // margin-bottom 20px).
    const counterRotatePopup = (popup) => {
        const el = popup._container;
        if (!belongsToRotatedMap(popup._map) || !el) return;
        const anchor = L.point(popup._getAnchor());
        const pivot = popup._zoomAnimated
            ? anchor.multiplyBy(-1)
            : popup._map.latLngToLayerPoint(popup._latlng);
        const style = getComputedStyle(el);
        const marginLeft = parseFloat(style.marginLeft) || 0;
        const marginBottom = parseFloat(style.marginBottom) || 0;
        appendCounterRotation(el,
            `${pivot.x - popup._containerLeft - marginLeft}px ` +
            `calc(100% + ${pivot.y + popup._containerBottom + marginBottom}px)`);
    };
    const popupUpdatePosition = L.Popup.prototype._updatePosition;
    L.Popup.prototype._updatePosition = function () {
        popupUpdatePosition.call(this);
        counterRotatePopup(this);
    };
    const popupAnimateZoom = L.Popup.prototype._animateZoom;
    L.Popup.prototype._animateZoom = function (e) {
        popupAnimateZoom.call(this, e);
        counterRotatePopup(this);
    };
}

/**
 * Makes `map` the rotatable map: pointer positions are un-rotated from
 * here on. Resets any rotation left over from a previous map.
 */
export function attachRotation(map) {
    rotatedMap = map;
    rotationDeg = 0;
    const original = map.mouseEventToContainerPoint;
    map.mouseEventToContainerPoint = function (e) {
        if (rotationDeg % 360 === 0) return original.call(this, e);
        return toContainerPoint(e.clientX, e.clientY);
    };
    writeRotation(0);
}

/** Forgets the map and clears every rotation trace it left behind. */
export function detachRotation() {
    if (rotatedMap) writeRotation(0);
    rotatedMap = null;
    rotationDeg = 0;
}

/** Current continuous rotation in degrees (map heading up). */
export function getRotationDeg() {
    return rotationDeg;
}

/**
 * Rotates the map so `headingDeg` points up. Returns false when the
 * change is under minStepDeg (sub-degree COG jitter would otherwise
 * restyle the container on every fix).
 */
export function setRotation(headingDeg, minStepDeg = 0) {
    if (!rotatedMap) return false;
    const next = unwrapRotation(rotationDeg, headingDeg);
    if (Math.abs(next - rotationDeg) < minStepDeg) return false;
    rotationDeg = next;
    writeRotation(next);
    return true;
}

function writeRotation(deg) {
    const container = activeContainer();
    if (!container) return;
    // A full turn is still north up; keep the accumulated angle so the
    // next transition starts from where the previous one ended.
    const rotated = Math.abs(normalizeDeltaDeg(deg)) > 1e-9;
    if (!rotated && deg === 0) {
        container.style.transform = '';
        container.style.removeProperty('--map-rot');
        delete container.dataset.mapRotated;
        return;
    }
    container.style.transformOrigin = 'center center';
    container.style.transform = `rotate(${-deg}deg)`;
    container.style.setProperty('--map-rot', `${deg}deg`);
    container.dataset.mapRotated = rotated ? '1' : '0';
}

/** Client coordinates -> container point of the rotatable map. */
export function toContainerPoint(clientX, clientY) {
    const c = activeContainer();
    const p = clientToContainerPoint(clientX, clientY, c.getBoundingClientRect(),
        c.offsetWidth, c.offsetHeight, rotationDeg, c.clientLeft, c.clientTop);
    return L.point(p.x, p.y);
}

/** Container point -> point on screen within the container's layout box. */
export function toLayoutPoint(x, y) {
    const c = activeContainer();
    if (!c || rotationDeg % 360 === 0) return { x, y };
    return containerPointToLayoutPoint(x, y, c.offsetWidth, c.offsetHeight, rotationDeg);
}

/** Screen vector -> container-frame vector (pans, follow offset). */
export function toContainerVector(x, y) {
    return rotationDeg % 360 === 0 ? { x, y } : rotateVector(x, y, rotationDeg);
}
