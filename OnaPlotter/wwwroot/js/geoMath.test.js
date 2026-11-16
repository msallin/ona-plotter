// Tests for geoMath.js pure math functions.
// Run with: node --test OnaPlotter/wwwroot/js/geoMath.test.js

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
    haversineMeters, bearingDeg, destPoint, vectorEnd, chartBoundsBoxes,
} from './geoMath.js';

// --- haversineMeters ---

describe('haversineMeters', () => {
    it('returns 0 for same point', () => {
        assert.equal(haversineMeters(47, 8, 47, 8), 0);
    });

    it('calculates Zurich to Bern (~95 km)', () => {
        const dist = haversineMeters(47.3769, 8.5417, 46.9480, 7.4474);
        assert.ok(dist > 95000 && dist < 100000, `Expected ~95-100km, got ${dist}`);
    });

    it('calculates 1 degree of latitude (~111 km)', () => {
        const dist = haversineMeters(0, 0, 1, 0);
        assert.ok(dist > 111000 && dist < 112000, `Expected ~111km, got ${dist}`);
    });

    it('is symmetric', () => {
        const d1 = haversineMeters(47, 8, 48, 9);
        const d2 = haversineMeters(48, 9, 47, 8);
        assert.ok(Math.abs(d1 - d2) < 0.01);
    });
});

// --- bearingDeg ---

describe('bearingDeg', () => {
    it('due north is 0', () => {
        const brg = bearingDeg(0, 0, 1, 0);
        assert.ok(Math.abs(brg) < 0.1 || Math.abs(brg - 360) < 0.1);
    });

    it('due east is 90', () => {
        const brg = bearingDeg(0, 0, 0, 1);
        assert.ok(Math.abs(brg - 90) < 0.5, `Expected ~90, got ${brg}`);
    });

    it('due south is 180', () => {
        const brg = bearingDeg(0, 0, -1, 0);
        assert.ok(Math.abs(brg - 180) < 0.1, `Expected ~180, got ${brg}`);
    });

    it('due west is 270', () => {
        const brg = bearingDeg(0, 0, 0, -1);
        assert.ok(Math.abs(brg - 270) < 0.5, `Expected ~270, got ${brg}`);
    });

    it('always returns 0-360 range', () => {
        const brg = bearingDeg(10, 10, 9, 9);
        assert.ok(brg >= 0 && brg < 360);
    });
});

// --- destPoint ---

describe('destPoint', () => {
    it('0 distance returns same point', () => {
        const [lat, lon] = destPoint(47, 8, 0, 0);
        assert.ok(Math.abs(lat - 47) < 0.0001);
        assert.ok(Math.abs(lon - 8) < 0.0001);
    });

    it('heading north increases latitude', () => {
        const [lat] = destPoint(47, 8, 0, 10000); // 10km north
        assert.ok(lat > 47);
    });

    it('heading east increases longitude', () => {
        const [, lon] = destPoint(47, 8, Math.PI / 2, 10000); // 10km east
        assert.ok(lon > 8);
    });
});

// --- vectorEnd ---

describe('vectorEnd', () => {
    it('returns null for null COG', () => {
        assert.equal(vectorEnd(47, 8, null, 5), null);
    });

    it('returns null for null SOG', () => {
        assert.equal(vectorEnd(47, 8, 0, null), null);
    });

    it('returns null for very low speed', () => {
        assert.equal(vectorEnd(47, 8, 0, 0.05), null);
    });

    it('returns point ahead for valid inputs', () => {
        const result = vectorEnd(47, 8, 0, 5); // 5 m/s north
        assert.ok(result !== null);
        assert.ok(result[0] > 47); // moved north
    });
});

// --- chartBoundsBoxes ---

describe('chartBoundsBoxes', () => {
    it('passes ordinary bounds through as one box', () => {
        // A Baltic chart.
        assert.deepEqual(chartBoundsBoxes([9.5, 53.8, 14.2, 55.1]),
            [[[53.8, 9.5], [55.1, 14.2]]]);
    });

    it('unwraps a chart crossing the antimeridian instead of inverting it', () => {
        // Fiji: 177 E to 178 W.
        assert.deepEqual(chartBoundsBoxes([177, -19, -178, -16]), [
            [[-19, 177], [-16, 182]],
            [[-19, -183], [-16, -178]],
        ]);
    });

    it('keeps a zero-width or whole-world chart as one box', () => {
        assert.deepEqual(chartBoundsBoxes([10, 0, 10, 1]), [[[0, 10], [1, 10]]]);
        assert.deepEqual(chartBoundsBoxes([-180, -85, 180, 85]), [[[-85, -180], [85, 180]]]);
    });

    it('rejects unusable bounds', () => {
        assert.equal(chartBoundsBoxes(null), null);
        assert.equal(chartBoundsBoxes([1, 2, 3]), null);
        assert.equal(chartBoundsBoxes([NaN, 0, 1, 1]), null);
        assert.equal(chartBoundsBoxes([0, -91, 1, 1]), null);
        assert.equal(chartBoundsBoxes([0, 0, 1, 91]), null);
        assert.equal(chartBoundsBoxes([0, 5, 1, 4]), null);
        assert.equal(chartBoundsBoxes([-541, 0, 1, 1]), null);
    });
});
