// Tests for geomPixels.js (pure 2D Euclidean helpers).
// Run with: node --test OnaPlotter/wwwroot/js/geomPixels.test.js
//
// Existence rationale: the route-edit + measure modules find "the
// closest segment to a click" by sweeping pointToSegmentPixels and
// picking the minimum. A regression that off-by-ones the projection
// (e.g. flips the t-clamp inequality) would silently land vertex
// inserts at the wrong segment and the helm would tap on a leg they
// can see but the code can't pick. Pure-math, no Leaflet.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { pointToSegmentPixels } from './geomPixels.js';

const EPS = 1e-9;

describe('pointToSegmentPixels: foot inside segment', () => {
    it('perpendicular distance to a horizontal segment', () => {
        const a = { x: 0, y: 0 };
        const b = { x: 10, y: 0 };
        const p = { x: 5, y: 3 };
        assert.ok(Math.abs(pointToSegmentPixels(p, a, b) - 3) < EPS);
    });

    it('perpendicular distance to a vertical segment', () => {
        const a = { x: 0, y: 0 };
        const b = { x: 0, y: 10 };
        const p = { x: 4, y: 5 };
        assert.ok(Math.abs(pointToSegmentPixels(p, a, b) - 4) < EPS);
    });

    it('perpendicular distance to a 45-degree segment', () => {
        // Segment from (0,0) to (10,10); probe at (10,0). Foot is (5,5).
        // Distance = sqrt(50) = ~7.0710678.
        const d = pointToSegmentPixels(
            { x: 10, y: 0 }, { x: 0, y: 0 }, { x: 10, y: 10 });
        assert.ok(Math.abs(d - Math.sqrt(50)) < 1e-7);
    });

    it('point on the segment returns zero', () => {
        // Probe lies exactly on the line at the midpoint.
        const d = pointToSegmentPixels(
            { x: 5, y: 0 }, { x: 0, y: 0 }, { x: 10, y: 0 });
        assert.ok(d < EPS);
    });
});

describe('pointToSegmentPixels: foot outside segment (endpoint clamp)', () => {
    it('past A end returns distance to A', () => {
        // Probe at (-3, 4); segment 0..10 on x-axis; closest is A.
        const d = pointToSegmentPixels(
            { x: -3, y: 4 }, { x: 0, y: 0 }, { x: 10, y: 0 });
        assert.ok(Math.abs(d - 5) < EPS);   // sqrt(9+16) = 5
    });

    it('past B end returns distance to B', () => {
        // Probe at (13, 4); segment 0..10 on x-axis; closest is B(10,0).
        const d = pointToSegmentPixels(
            { x: 13, y: 4 }, { x: 0, y: 0 }, { x: 10, y: 0 });
        assert.ok(Math.abs(d - 5) < EPS);   // sqrt(9+16) = 5
    });

    it('exactly at A returns zero', () => {
        const d = pointToSegmentPixels(
            { x: 0, y: 0 }, { x: 0, y: 0 }, { x: 10, y: 0 });
        assert.ok(d < EPS);
    });

    it('exactly at B returns zero', () => {
        const d = pointToSegmentPixels(
            { x: 10, y: 0 }, { x: 0, y: 0 }, { x: 10, y: 0 });
        assert.ok(d < EPS);
    });
});

describe('pointToSegmentPixels: degenerate zero-length segment', () => {
    it('A == B falls back to point distance (no NaN)', () => {
        // Coincident endpoints would otherwise divide by zero in the
        // projection. The implementation early-returns Math.hypot(p, a).
        const a = { x: 5, y: 5 };
        const d = pointToSegmentPixels({ x: 8, y: 9 }, a, a);
        assert.ok(Math.abs(d - 5) < EPS);   // sqrt(9+16) = 5
        assert.ok(!Number.isNaN(d));
    });

    it('A == B == probe returns zero', () => {
        const p = { x: 0, y: 0 };
        assert.ok(pointToSegmentPixels(p, p, p) < EPS);
    });
});

describe('pointToSegmentPixels: closest-segment selection', () => {
    it('picks the nearer segment of two', () => {
        // The route-edit / measure use case: given a click, pick the
        // segment closest to it. Pin the relative ordering so a
        // regression to "always pick first" or "off-by-one direction"
        // surfaces here.
        const click = { x: 5, y: 3 };
        const segA = { from: { x: 0, y: 0 }, to: { x: 10, y: 0 } };  // perpendicular distance 3
        const segB = { from: { x: 0, y: 7 }, to: { x: 10, y: 7 } };  // perpendicular distance 4
        const dA = pointToSegmentPixels(click, segA.from, segA.to);
        const dB = pointToSegmentPixels(click, segB.from, segB.to);
        assert.ok(dA < dB);
        assert.ok(Math.abs(dA - 3) < EPS);
        assert.ok(Math.abs(dB - 4) < EPS);
    });
});
