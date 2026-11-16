// Tests for mapRotation.js coordinate transforms.
// Run with: node --test OnaPlotter/wwwroot/js/mapRotation.test.js

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
    normalizeDeltaDeg, unwrapRotation, rotateVector,
    clientToContainerPoint, containerPointToLayoutPoint, overscanFor,
    attachRotation, detachRotation, setRotation, setOverscanEnabled,
    getOverscan, fitPadding, toLayoutPoint, viewportSize, unrotatedLayer,
} from './mapRotation.js';

const near = (actual, expected, eps = 1e-9) =>
    assert.ok(Math.abs(actual - expected) < eps, `${actual} !~ ${expected}`);
const nearPt = (p, x, y, eps = 1e-9) => { near(p.x, x, eps); near(p.y, y, eps); };

// Bounding rect of a w x h box rotated by -deg about its centre (cx, cy).
function rotatedRect(cx, cy, w, h, deg) {
    const r = Math.abs(deg * Math.PI / 180);
    const bw = Math.abs(w * Math.cos(r)) + Math.abs(h * Math.sin(r));
    const bh = Math.abs(w * Math.sin(r)) + Math.abs(h * Math.cos(r));
    return { left: cx - bw / 2, top: cy - bh / 2, width: bw, height: bh };
}

describe('normalizeDeltaDeg', () => {
    it('maps into (-180, 180]', () => {
        assert.equal(normalizeDeltaDeg(0), 0);
        assert.equal(normalizeDeltaDeg(180), 180);
        assert.equal(normalizeDeltaDeg(-180), 180);
        assert.equal(normalizeDeltaDeg(181), -179);
        assert.equal(normalizeDeltaDeg(358), -2);
        assert.equal(normalizeDeltaDeg(-358), 2);
        assert.equal(normalizeDeltaDeg(720 + 10), 10);
    });
});

describe('unwrapRotation', () => {
    it('crossing north takes the short way', () => {
        // COG 359 -> 1: the container must turn 2 deg, not 358.
        assert.equal(unwrapRotation(359, 1), 361);
        assert.equal(unwrapRotation(1, 359), -1);
    });

    it('keeps accumulating across several turns', () => {
        let r = 0;
        for (const cog of [90, 180, 270, 0, 90]) r = unwrapRotation(r, cog);
        assert.equal(r, 450);
    });

    it('returning to north from an accumulated angle stays continuous', () => {
        assert.equal(unwrapRotation(361, 0), 360);
        assert.equal(unwrapRotation(-359, 0), -360);
    });

    it('a target equal to the current angle is a no-op', () => {
        assert.equal(unwrapRotation(42, 42), 42);
        assert.equal(unwrapRotation(402, 42), 402);
    });
});

describe('rotateVector', () => {
    it('rotates clockwise in screen coordinates (y down)', () => {
        nearPt(rotateVector(1, 0, 90), 0, 1);
        nearPt(rotateVector(0, 1, 90), -1, 0);
        nearPt(rotateVector(1, 0, -90), 0, -1);
    });

    it('preserves length', () => {
        const v = rotateVector(3, 4, 37);
        near(Math.hypot(v.x, v.y), 5);
    });
});

describe('clientToContainerPoint', () => {
    const w = 800, h = 600, cx = 500, cy = 400;

    it('is the plain offset when the map is north up', () => {
        const rect = { left: cx - w / 2, top: cy - h / 2, width: w, height: h };
        nearPt(clientToContainerPoint(110, 120, rect, w, h, 0), 110 - 100, 120 - 100);
    });

    it('maps the centre to the centre at any angle', () => {
        for (const deg of [0, 17, 90, 180, 271, -45]) {
            const rect = rotatedRect(cx, cy, w, h, deg);
            nearPt(clientToContainerPoint(cx, cy, rect, w, h, deg), w / 2, h / 2);
        }
    });

    it('a tap above the centre in course-up 90 lands right of centre in the map frame', () => {
        // COG 090: map rotated -90 so east points up on screen. A tap
        // 100 px above the centre is 100 px east of the boat, i.e. to the
        // right in the unrotated container.
        const rect = rotatedRect(cx, cy, w, h, 90);
        nearPt(clientToContainerPoint(cx, cy - 100, rect, w, h, 90), w / 2 + 100, h / 2);
    });

    it('round-trips with containerPointToLayoutPoint', () => {
        // Layout box top-left at (cx - w/2, cy - h/2); the context menu
        // lands where the finger was.
        for (const deg of [0, 33, 145, 200, -80]) {
            const rect = rotatedRect(cx, cy, w, h, deg);
            const p = clientToContainerPoint(130, 650, rect, w, h, deg);
            const back = containerPointToLayoutPoint(p.x, p.y, w, h, deg);
            nearPt(back, 130 - (cx - w / 2), 650 - (cy - h / 2), 1e-6);
        }
    });

    it('subtracts the container border like Leaflet does', () => {
        const rect = { left: 0, top: 0, width: w, height: h };
        nearPt(clientToContainerPoint(10, 10, rect, w, h, 0, 2, 3), 8, 7);
    });
});

describe('overscanFor', () => {
    it('grows each side to the half-diagonal (390 x 844 phone)', () => {
        // diagonal 929.7 -> half 464.9; x: 464.9 - 195 -> 270, y: 464.9 - 422 -> 43
        assert.deepEqual(overscanFor(390, 844), { x: 270, y: 43 });
    });

    it('square viewport grows equally on both axes', () => {
        const o = overscanFor(1000, 1000);
        assert.equal(o.x, o.y);
        assert.equal(o.x, Math.ceil(Math.SQRT2 * 500 - 500));
    });

    it('degenerate sizes do not grow', () => {
        assert.deepEqual(overscanFor(0, 0), { x: 0, y: 0 });
        assert.deepEqual(overscanFor(0, 500), { x: 0, y: 0 });
        assert.deepEqual(overscanFor(-1, 500), { x: 0, y: 0 });
        assert.deepEqual(overscanFor(NaN, 500), { x: 0, y: 0 });
    });

    it('whole pixels per side, never shrinking', () => {
        for (const [w, h] of [[1, 1], [375, 667], [768, 1024], [1920, 1080], [2732, 2048]]) {
            const o = overscanFor(w, h);
            assert.ok(Number.isInteger(o.x) && Number.isInteger(o.y));
            assert.ok(o.x >= 0 && o.y >= 0);
        }
    });
});

describe('oversized rotated container', () => {
    // Real-world case: a phone (portrait) and an iPad (landscape) in
    // course-up. The container is the viewport plus overscanFor on each
    // side, centred on the viewport, and rotated about its centre.
    const viewports = [[390, 844], [1180, 820]];
    const angles = [0, 1, 17, 45, 90, 135, 180, 233, 270, 315, -30];

    for (const [vw, vh] of viewports) {
        const o = overscanFor(vw, vh);
        const cw = vw + 2 * o.x, ch = vh + 2 * o.y;
        const cx = vw / 2, cy = vh / 2;   // viewport at client (0, 0)

        it(`${vw}x${vh}: every screen corner lands inside the map at any angle`, () => {
            for (const deg of angles) {
                const rect = rotatedRect(cx, cy, cw, ch, deg);
                for (const [x, y] of [[0, 0], [vw, 0], [0, vh], [vw, vh]]) {
                    const p = clientToContainerPoint(x, y, rect, cw, ch, deg);
                    assert.ok(p.x >= -1e-6 && p.x <= cw + 1e-6 && p.y >= -1e-6 && p.y <= ch + 1e-6,
                        `corner (${x}, ${y}) at ${deg} deg -> (${p.x}, ${p.y}) outside ${cw}x${ch}`);
                }
            }
        });

        it(`${vw}x${vh}: the screen centre is the container centre (boat stays put)`, () => {
            for (const deg of angles) {
                const rect = rotatedRect(cx, cy, cw, ch, deg);
                nearPt(clientToContainerPoint(cx, cy, rect, cw, ch, deg), cw / 2, ch / 2);
            }
        });

        it(`${vw}x${vh}: a tap round-trips to the same viewport point`, () => {
            // The context menu is placed in viewport coordinates: the
            // layout point minus the overscan.
            for (const deg of angles) {
                const rect = rotatedRect(cx, cy, cw, ch, deg);
                const p = clientToContainerPoint(37, vh - 12, rect, cw, ch, deg);
                const back = containerPointToLayoutPoint(p.x, p.y, cw, ch, deg);
                nearPt({ x: back.x - o.x, y: back.y - o.y }, 37, vh - 12, 1e-6);
            }
        });
    }
});

// Minimal DOM stand-ins: a viewport element holding the map container,
// and a Leaflet-map shaped object that records invalidateSize calls.
function fakeMap(vw, vh) {
    const props = new Map();
    const style = {
        transform: '', transformOrigin: '',
        setProperty: (k, v) => props.set(k, v),
        removeProperty: (k) => props.delete(k),
    };
    const appended = [];
    const viewport = { clientWidth: vw, clientHeight: vh, appendChild: (el) => appended.push(el) };
    const container = { style, parentElement: viewport, offsetWidth: vw, offsetHeight: vh };
    const invalidations = [];
    const controls = { id: 'controls' };
    const map = {
        _controlContainer: controls,
        getContainer: () => container,
        mouseEventToContainerPoint: () => null,
        invalidateSize: (opts) => {
            invalidations.push(opts);
            const o = getOverscan();
            container.offsetWidth = vw + 2 * o.x;
            container.offsetHeight = vh + 2 * o.y;
        },
    };
    return { map, props, style, viewport, appended, invalidations, controls };
}

describe('rotation state on a map', () => {
    it('attach moves the control corners into the unrotated viewport', () => {
        const f = fakeMap(390, 844);
        attachRotation(f.map);
        assert.deepEqual(f.appended, [f.controls]);
        assert.equal(unrotatedLayer(), f.viewport);
        assert.deepEqual(viewportSize(), { x: 390, y: 844 });
        detachRotation();
    });

    it('north up never writes a transform', () => {
        const f = fakeMap(390, 844);
        attachRotation(f.map);
        assert.equal(setRotation(0), false);
        assert.equal(f.style.transform, '');
        assert.equal(f.props.has('--map-rot'), false);
        detachRotation();
    });

    it('enabling overscan grows the container once and keeps the centre', () => {
        const f = fakeMap(390, 844);
        attachRotation(f.map);
        setOverscanEnabled(true);
        assert.deepEqual(getOverscan(), { x: 270, y: 43 });
        assert.equal(f.props.get('--map-overscan-x'), '270px');
        assert.equal(f.props.get('--map-overscan-y'), '43px');
        assert.deepEqual(f.invalidations, [{ pan: true, animate: false }]);
        // Same viewport, same growth: no second resize.
        setOverscanEnabled(true);
        assert.equal(f.invalidations.length, 1);
        detachRotation();
    });

    it('fitBounds padding and layout points account for the overscan', () => {
        const f = fakeMap(390, 844);
        attachRotation(f.map);
        assert.deepEqual(fitPadding(40), [40, 40]);
        setOverscanEnabled(true);
        assert.deepEqual(fitPadding(40), [310, 83]);
        // North-up angle with overscan: container centre is viewport centre.
        nearPt(toLayoutPoint(270 + 195, 43 + 422), 195, 422);
        detachRotation();
    });

    it('rotation writes the shared --map-rot and keeps it through north', () => {
        const f = fakeMap(390, 844);
        attachRotation(f.map);
        setOverscanEnabled(true);
        assert.equal(setRotation(90), true);
        assert.equal(f.props.get('--map-rot'), '90deg');
        assert.equal(f.style.transform, 'rotate(calc(-1 * var(--map-rot, 0deg)))');
        // Back to north: the transform stays so the --map-rot transition
        // can finish turning the chart and its labels together.
        assert.equal(setRotation(0), true);
        assert.equal(f.props.get('--map-rot'), '0deg');
        assert.notEqual(f.style.transform, '');
        // Sub-step jitter is ignored.
        assert.equal(setRotation(0.5, 1), false);
        detachRotation();
        assert.equal(f.style.transform, '');
        assert.equal(f.props.has('--map-rot'), false);
        assert.equal(f.props.has('--map-overscan-x'), false);
    });

    it('disabling overscan shrinks back to the viewport', () => {
        const f = fakeMap(1180, 820);
        attachRotation(f.map);
        setOverscanEnabled(true);
        setOverscanEnabled(false);
        assert.deepEqual(getOverscan(), { x: 0, y: 0 });
        assert.equal(f.props.has('--map-overscan-x'), false);
        assert.equal(f.invalidations.length, 2);
        detachRotation();
    });
});
