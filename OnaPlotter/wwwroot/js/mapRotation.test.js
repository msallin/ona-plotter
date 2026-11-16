// Tests for mapRotation.js coordinate transforms.
// Run with: node --test OnaPlotter/wwwroot/js/mapRotation.test.js

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
    normalizeDeltaDeg, unwrapRotation, rotateVector,
    clientToContainerPoint, containerPointToLayoutPoint,
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

    it('a tap above the centre in course-up 90 lands left of centre in the map frame', () => {
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
