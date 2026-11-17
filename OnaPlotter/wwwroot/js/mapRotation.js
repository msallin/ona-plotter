/**
 * Course-up / head-up rotation for the chart map.
 *
 * The map container is rotated with a CSS transform (rotate(-deg) about
 * its centre). It sits inside an unrotated, overflow-clipped viewport
 * (its parent element). While rotation is enabled the container is
 * oversized to the viewport diagonal and centred on it (see
 * overscanFor), so the rotated chart still covers the screen corners;
 * Leaflet's control corners (attribution) and the range-scale chip are
 * moved into the viewport so they stay upright and on screen. Leaflet
 * itself knows nothing about the rotation, so every place where it
 * turns a screen position into a map position has to be corrected
 * here:
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
 *
 * The angle lives in one registered custom property, --map-rot, on the
 * container (app.css declares it with @property and transitions it).
 * The container's own rotate() and every counter-rotation (AIS labels,
 * tooltips, popups) read that one animated value, so they turn in
 * lockstep through an orientation change instead of labels snapping
 * upright while the chart is still turning.
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

/**
 * Per-side growth that makes a container centred on a width x height
 * viewport cover it at any rotation: each half-extent must reach the
 * viewport's half-diagonal. Whole pixels per side keep the container
 * centred on the viewport without sub-pixel tile offsets.
 *
 * Example: 390 x 844 phone -> diagonal 929.7, so x 270, y 43
 * (container 930 x 930).
 */
export function overscanFor(width, height) {
    if (!(width > 0) || !(height > 0)) return { x: 0, y: 0 };
    const half = Math.hypot(width, height) / 2;
    return {
        x: Math.max(0, Math.ceil(half - width / 2)),
        y: Math.max(0, Math.ceil(half - height / 2)),
    };
}

// Rotation state of the one map that can rotate (the chart page).
let rotatedMap = null;
let rotationDeg = 0;
let installed = false;
// Oversize state: whether course-up / head-up is on, and the growth
// per side currently applied to the container.
let overscanEnabled = false;
let overscan = { x: 0, y: 0 };
let viewportObserver = null;

function activeContainer() {
    return rotatedMap ? rotatedMap.getContainer() : null;
}

/**
 * The unrotated element around the map container. Hosts everything that
 * must stay upright and inside the visible screen (Leaflet's control
 * corners, the range-scale chip).
 */
export function unrotatedLayer() {
    const c = activeContainer();
    return c ? c.parentElement : null;
}

/** Visible map size: the viewport, not the oversized container. */
export function viewportSize() {
    const vp = unrotatedLayer();
    return vp ? { x: vp.clientWidth, y: vp.clientHeight } : { x: 0, y: 0 };
}

function belongsToRotatedMap(map) {
    return !!map && map === rotatedMap;
}

// Inline transform suffix that counter-rotates an overlay. Reads the
// angle from --map-rot on the container so a rotation change (and its
// transition) restyles every open tooltip / popup without touching
// each element.
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
 * here on, and Leaflet's control corners move to the unrotated parent
 * so they never turn with the chart or sit in the oversized margin.
 * Resets any rotation left over from a previous map.
 */
export function attachRotation(map) {
    rotatedMap = map;
    rotationDeg = 0;
    overscanEnabled = false;
    overscan = { x: 0, y: 0 };
    const original = map.mouseEventToContainerPoint;
    map.mouseEventToContainerPoint = function (e) {
        if (rotationDeg % 360 === 0) return original.call(this, e);
        return toContainerPoint(e.clientX, e.clientY);
    };
    const layer = unrotatedLayer();
    // Leaflet removes the control container with DomUtil.remove (parent
    // agnostic) on map.remove(), so re-parenting it is safe.
    if (layer && map._controlContainer) layer.appendChild(map._controlContainer);
    if (layer && typeof ResizeObserver !== 'undefined') {
        viewportObserver = new ResizeObserver(() => applyOverscan());
        viewportObserver.observe(layer);
    }
    clearRotation();
    writeOverscan();
}

/** Forgets the map and clears every rotation trace it left behind. */
export function detachRotation() {
    if (viewportObserver) { viewportObserver.disconnect(); viewportObserver = null; }
    if (rotatedMap) {
        clearRotation();
        overscan = { x: 0, y: 0 };
        writeOverscan();
    }
    rotatedMap = null;
    rotationDeg = 0;
    overscanEnabled = false;
}

/**
 * Turns the oversized container on (course-up / head-up) or off (north
 * up). North up keeps the container at viewport size so it loads no
 * tiles nobody can see.
 */
export function setOverscanEnabled(enabled) {
    overscanEnabled = !!enabled;
    applyOverscan();
}

/** Current growth per side in px (zero while north up). */
export function getOverscan() {
    return overscan;
}

/**
 * fitBounds padding that fits into the visible viewport rather than the
 * oversized container: `px` on every side plus the growth per side.
 */
export function fitPadding(px) {
    return [px + overscan.x, px + overscan.y];
}

// Recomputes the growth for the current viewport and resizes the map
// when it changed. invalidateSize with pan keeps the geographic centre
// at the container centre, and the container grows symmetrically
// about the viewport centre, so the chart does not jump on screen.
function applyOverscan() {
    if (!rotatedMap) return;
    const vp = viewportSize();
    const next = overscanEnabled ? overscanFor(vp.x, vp.y) : { x: 0, y: 0 };
    if (next.x === overscan.x && next.y === overscan.y) return;
    overscan = next;
    writeOverscan();
    rotatedMap.invalidateSize({ pan: true, animate: false });
}

function writeOverscan() {
    const container = activeContainer();
    if (!container) return;
    if (overscan.x === 0 && overscan.y === 0) {
        container.style.removeProperty('--map-overscan-x');
        container.style.removeProperty('--map-overscan-y');
        return;
    }
    container.style.setProperty('--map-overscan-x', `${overscan.x}px`);
    container.style.setProperty('--map-overscan-y', `${overscan.y}px`);
}

/**
 * Rotates the map so `headingDeg` points up. Returns false when the
 * change is under minStepDeg (sub-degree COG jitter would otherwise
 * restyle the container on every fix).
 */
export function setRotation(headingDeg, minStepDeg = 0) {
    if (!rotatedMap) return false;
    const next = unwrapRotation(rotationDeg, headingDeg);
    // An unchanged angle writes nothing, so a north-up session never
    // puts a transform on the container.
    if (next === rotationDeg || Math.abs(next - rotationDeg) < minStepDeg) return false;
    rotationDeg = next;
    writeRotation(next);
    return true;
}

// Writes the angle. Once rotated, the transform stays on the container
// even back at north (--map-rot 0deg, or a full turn): clearing it while
// --map-rot is still transitioning would snap the chart upright under
// labels that are still turning. Only attach / detach clear it.
function writeRotation(deg) {
    const container = activeContainer();
    if (!container) return;
    container.style.transformOrigin = 'center center';
    container.style.transform = 'rotate(calc(-1 * var(--map-rot, 0deg)))';
    container.style.setProperty('--map-rot', `${deg}deg`);
}

function clearRotation() {
    const container = activeContainer();
    if (!container) return;
    container.style.transform = '';
    container.style.removeProperty('--map-rot');
}

/** Client coordinates -> container point of the rotatable map. */
export function toContainerPoint(clientX, clientY) {
    const c = activeContainer();
    const p = clientToContainerPoint(clientX, clientY, c.getBoundingClientRect(),
        c.offsetWidth, c.offsetHeight, rotationDeg, c.clientLeft, c.clientTop);
    return L.point(p.x, p.y);
}

/**
 * Container point -> point on screen within the viewport (the
 * unrotated parent), which is what an absolutely positioned sibling of
 * the map such as the context menu needs.
 */
export function toLayoutPoint(x, y) {
    const c = activeContainer();
    if (!c) return { x, y };
    const p = rotationDeg % 360 === 0
        ? { x, y }
        : containerPointToLayoutPoint(x, y, c.offsetWidth, c.offsetHeight, rotationDeg);
    return { x: p.x - overscan.x, y: p.y - overscan.y };
}

/** Screen vector -> container-frame vector (pans, follow offset). */
export function toContainerVector(x, y) {
    return rotationDeg % 360 === 0 ? { x, y } : rotateVector(x, y, rotationDeg);
}
