// Tests for format.js render-time formatters.
// Run with: node --test OnaPlotter/wwwroot/js/format.test.js
//
// latLonDms shares its vectors with FormatTests.LatLonDms_* on the C#
// side (Format.LatLonDms renders the same string in Razor views).

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { mobElapsed, latLonDms, latDms, lonDms, rangeRingLabel, measureDistance } from './format.js';

describe('mobElapsed', () => {
    it('clamps negative input to T+0s', () => {
        assert.equal(mobElapsed(-5), 'T+0s');
    });
    it('renders seconds under a minute', () => {
        assert.equal(mobElapsed(0), 'T+0s');
        assert.equal(mobElapsed(45), 'T+45s');
        assert.equal(mobElapsed(59), 'T+59s');
        assert.equal(mobElapsed(59.9), 'T+59s');
    });
    it('rolls to whole minutes at 60 s', () => {
        assert.equal(mobElapsed(60), 'T+1m');
        assert.equal(mobElapsed(125), 'T+2m');
        assert.equal(mobElapsed(3599), 'T+59m');
    });
    it('rolls to hours + minutes at one hour', () => {
        assert.equal(mobElapsed(3600), 'T+1h0m');
        assert.equal(mobElapsed(3660), 'T+1h1m');
        assert.equal(mobElapsed(5025), 'T+1h23m');
    });
});

describe('latLonDms', () => {
    it('north-east', () => {
        assert.equal(latLonDms(47.5, 8.5), '47.50000°N 8.50000°E');
    });
    it('south-west', () => {
        assert.equal(latLonDms(-25.535966, -76.761572), '25.53597°S 76.76157°W');
    });
    it('always five decimals', () => {
        assert.equal(latLonDms(0, 0), '0.00000°N 0.00000°E');
    });
    it('custom separator', () => {
        assert.equal(latLonDms(47.5, -8.5, ', '), '47.50000°N, 8.50000°W');
    });
    it('single-axis helpers', () => {
        assert.equal(latDms(-1.25), '1.25000°S');
        assert.equal(lonDms(170), '170.00000°E');
    });
});

describe('rangeRingLabel', () => {
    it('zero or negative -> empty', () => {
        assert.equal(rangeRingLabel(0), '');
        assert.equal(rangeRingLabel(-1), '');
    });
    it('non-finite -> empty', () => {
        assert.equal(rangeRingLabel(NaN), '');
        assert.equal(rangeRingLabel(Infinity), '');
    });
    it('under 1 nm strips trailing zeros', () => {
        assert.equal(rangeRingLabel(0.5), '0.5 nm');
        assert.equal(rangeRingLabel(0.25), '0.25 nm');
        assert.equal(rangeRingLabel(0.1), '0.1 nm');
    });
    it('1 to 10 nm strips a trailing .0', () => {
        assert.equal(rangeRingLabel(1.0), '1 nm');
        assert.equal(rangeRingLabel(2.5), '2.5 nm');
        assert.equal(rangeRingLabel(9.99), '10 nm');
    });
    it('10 nm and above rounds to an integer', () => {
        assert.equal(rangeRingLabel(10), '10 nm');
        assert.equal(rangeRingLabel(50.5), '51 nm');
    });
});

describe('measureDistance', () => {
    it('short distance', () => {
        assert.equal(measureDistance(500), '0.27 nm / 500 m');
    });
    it('one nautical mile', () => {
        assert.equal(measureDistance(1852), '1.00 nm / 1852 m');
    });
    it('long distance keeps two decimals on nm', () => {
        assert.equal(measureDistance(46300), '25.00 nm / 46300 m');
    });
    it('zero', () => {
        assert.equal(measureDistance(0), '0.00 nm / 0 m');
    });
});
