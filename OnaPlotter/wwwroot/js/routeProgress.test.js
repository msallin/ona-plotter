// Tests for routeProgress.js. Pure JS, no Leaflet stub required.
// Run with: node --test OnaPlotter/wwwroot/js/routeProgress.test.js

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { splitRouteByProgress } from './routeProgress.js';

// Realistic four-waypoint route used by most tests below.
//   coords[0] -- coords[1] -- coords[2] -- coords[3]
//        leg 0       leg 1       leg 2
const ROUTE = [
    [47.00, 8.00],
    [47.10, 8.10],
    [47.20, 8.20],
    [47.30, 8.30],
];

describe('splitRouteByProgress -- typical progress states', () => {
    it('wpIdx = 0 (heading to first WP, nothing passed): all legs are future', () => {
        const { passed, future } = splitRouteByProgress(ROUTE, 0);
        assert.deepEqual(passed, []);
        assert.deepEqual(future, ROUTE);
    });

    it('wpIdx = 1 (past first WP, heading to second): no full passed leg, current+rest are future', () => {
        // The first WP has been reached but no full leg has been
        // *passed* yet -- you can only render a passed polyline once
        // there are two reached waypoints to connect.
        const { passed, future } = splitRouteByProgress(ROUTE, 1);
        assert.deepEqual(passed, []);
        assert.deepEqual(future, ROUTE); // starts at coords[0], includes current leg
    });

    it('wpIdx = 2 (past second WP, heading to third): leg 0 passed, current+rest future', () => {
        const { passed, future } = splitRouteByProgress(ROUTE, 2);
        assert.deepEqual(passed, [ROUTE[0], ROUTE[1]]);
        assert.deepEqual(future, [ROUTE[1], ROUTE[2], ROUTE[3]]);
    });

    it('wpIdx = 3 (heading to final WP): legs 0+1 passed, last leg is future', () => {
        const { passed, future } = splitRouteByProgress(ROUTE, 3);
        assert.deepEqual(passed, [ROUTE[0], ROUTE[1], ROUTE[2]]);
        assert.deepEqual(future, [ROUTE[2], ROUTE[3]]);
    });

    it('passed and future share the boundary point so the polylines render without a gap', () => {
        const { passed, future } = splitRouteByProgress(ROUTE, 2);
        assert.deepEqual(passed[passed.length - 1], future[0]);
    });
});

describe('splitRouteByProgress -- boundary and degenerate inputs', () => {
    it('empty array yields both segments empty', () => {
        const { passed, future } = splitRouteByProgress([], 0);
        assert.deepEqual(passed, []);
        assert.deepEqual(future, []);
    });

    it('single-point route is not drawable -- both segments empty', () => {
        const { passed, future } = splitRouteByProgress([[47, 8]], 0);
        assert.deepEqual(passed, []);
        assert.deepEqual(future, []);
    });

    it('two-point route, wpIdx 0: future is the whole route', () => {
        const r = [ROUTE[0], ROUTE[1]];
        const { passed, future } = splitRouteByProgress(r, 0);
        assert.deepEqual(passed, []);
        assert.deepEqual(future, r);
    });

    it('two-point route, wpIdx 1: nothing passed yet (current leg still in progress)', () => {
        // Second WP not yet reached: current leg coords[0]->coords[1]
        // is the future segment, no passed polyline.
        const r = [ROUTE[0], ROUTE[1]];
        const { passed, future } = splitRouteByProgress(r, 1);
        assert.deepEqual(passed, []);
        assert.deepEqual(future, r);
    });

    it('wpIdx beyond route end clamps so render does not crash', () => {
        // Realistic when boat overshoots the last WP and the C# side
        // still pushes the closest-match index. We must degrade
        // gracefully (no exception, no half-formed polyline).
        const { passed, future } = splitRouteByProgress(ROUTE, 99);
        assert.deepEqual(passed, ROUTE);
        assert.deepEqual(future, []);
    });

    it('negative wpIdx clamps to 0', () => {
        const { passed, future } = splitRouteByProgress(ROUTE, -3);
        assert.deepEqual(passed, []);
        assert.deepEqual(future, ROUTE);
    });

    it('non-array coords yields both segments empty', () => {
        const { passed, future } = splitRouteByProgress(null, 1);
        assert.deepEqual(passed, []);
        assert.deepEqual(future, []);
    });

    it('non-integer wpIdx is truncated to integer', () => {
        const { passed, future } = splitRouteByProgress(ROUTE, 2.7);
        // 2.7 | 0 == 2, so behaves identical to the wpIdx=2 case.
        assert.deepEqual(passed, [ROUTE[0], ROUTE[1]]);
        assert.deepEqual(future, [ROUTE[1], ROUTE[2], ROUTE[3]]);
    });
});

describe('splitRouteByProgress -- input safety', () => {
    it('does not mutate the caller\'s coords array', () => {
        const r = ROUTE.map(p => [...p]);
        const before = JSON.stringify(r);
        splitRouteByProgress(r, 2);
        assert.equal(JSON.stringify(r), before);
    });

    it('returned segments are independent slices (mutating output does not affect input)', () => {
        const r = ROUTE.map(p => [...p]);
        const { passed, future } = splitRouteByProgress(r, 2);
        passed.push([0, 0]);
        future.push([0, 0]);
        assert.equal(r.length, ROUTE.length);
    });
});
