// Tests for geoMath.js pure math functions.
// Run with: node --test OnaPlotter/wwwroot/js/geoMath.test.js

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
    haversineMeters, bearingDeg, destPoint, vectorEnd,
    speedColor, speedBucket,
    RAD, DEG, NM_PER_METER
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

// --- speedColor ---

describe('speedColor', () => {
    it('null returns blue', () => {
        assert.equal(speedColor(null), '#3b82f6');
    });

    it('0 speed returns blue-ish', () => {
        const color = speedColor(0);
        assert.ok(color.startsWith('rgb('));
    });

    it('high speed returns warm color', () => {
        const color = speedColor(10); // ~19 knots
        assert.ok(color.startsWith('rgb('));
        // Extract R channel - should be high (warm)
        const r = parseInt(color.match(/rgb\((\d+)/)[1]);
        assert.ok(r > 100, `Expected warm red channel, got ${r}`);
    });

    it('different speeds produce different colors', () => {
        assert.notEqual(speedColor(0), speedColor(5));
        assert.notEqual(speedColor(2), speedColor(8));
    });
});

// --- speedBucket ---

describe('speedBucket', () => {
    it('null returns 0', () => {
        assert.equal(speedBucket(null), 0);
    });

    it('0 m/s returns bucket 0', () => {
        assert.equal(speedBucket(0), 0);
    });

    it('0.5 m/s returns bucket 0', () => {
        assert.equal(speedBucket(0.5), 0);
    });

    it('1.5 m/s returns bucket 1', () => {
        assert.equal(speedBucket(1.5), 1);
    });

    it('10 m/s returns highest bucket', () => {
        assert.equal(speedBucket(10), 5);
    });

    it('bucket increases with speed', () => {
        const b0 = speedBucket(0);
        const b3 = speedBucket(3);
        const b8 = speedBucket(8);
        assert.ok(b0 <= b3);
        assert.ok(b3 <= b8);
    });
});
