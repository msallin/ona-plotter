// Tests for rangeScale.js. Vectors cover the ladder pick, the width
// clamp against the sample, the visible floor, and degenerate input.
// Run with: node --test OnaPlotter/wwwroot/js/rangeScale.test.js

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { pickRangeScale, RANGE_SCALE_NM_LADDER } from './rangeScale.js';

describe('pickRangeScale ladder pick', () => {
    it('picks the entry when the sample sits exactly on it', () => {
        const p = pickRangeScale(1.0, 200, 8);
        assert.equal(p.niceNm, 1);
        assert.equal(p.label, '1 nm');
    });

    it('picks the lower neighbour between entries', () => {
        const p = pickRangeScale(0.7, 200, 8);
        assert.equal(p.niceNm, 0.5);
        assert.equal(p.label, '0.5 nm');
    });

    it('sticks at the top entry for very large samples', () => {
        const p = pickRangeScale(750, 200, 8);
        assert.equal(p.niceNm, 500);
        assert.equal(p.label, '500 nm');
    });

    it('ladder is strictly ascending', () => {
        for (let i = 1; i < RANGE_SCALE_NM_LADDER.length; i++) {
            assert.ok(RANGE_SCALE_NM_LADDER[i] > RANGE_SCALE_NM_LADDER[i - 1]);
        }
    });
});

describe('pickRangeScale width', () => {
    it('below the floor picks the floor but caps width at the sample', () => {
        // Harbour zoom: 200 px covers 0.0027 nm (~5 m); 0.02 nm would
        // be wider than the sample, so the bar caps at 200 px.
        const p = pickRangeScale(0.0027, 200, 8);
        assert.equal(p.niceNm, 0.02);
        assert.equal(p.widthPx, 200);
    });

    it('at the floor exactly renders full width', () => {
        const p = pickRangeScale(0.02, 200, 8);
        assert.equal(p.niceNm, 0.02);
        assert.equal(p.widthPx, 200);
    });

    it('scales the bar by nice / sample', () => {
        // 1 nm of a 1.7 nm sample: round(200 / 1.7) = 118 px.
        const p = pickRangeScale(1.7, 200, 8);
        assert.equal(p.niceNm, 1);
        assert.equal(p.widthPx, 118);
    });

    it('an exact ladder entry renders the full sample width', () => {
        const p = pickRangeScale(2.0, 200, 8);
        assert.equal(p.widthPx, 200);
    });

    it('honours the minPx floor', () => {
        assert.equal(pickRangeScale(100, 200, 8).widthPx, 200);
        // 500 nm of a 20000 nm sample is 5 px, floored to 8.
        assert.equal(pickRangeScale(20000, 200, 8).widthPx, 8);
        assert.equal(pickRangeScale(20000, 200, 12).widthPx, 12);
    });
});

describe('pickRangeScale degenerate samples', () => {
    for (const sample of [0, -1, NaN, Infinity]) {
        it(`sample ${sample} returns the floor at minPx`, () => {
            const p = pickRangeScale(sample, 200, 8);
            assert.equal(p.niceNm, 0.02);
            assert.equal(p.widthPx, 8);
            assert.equal(p.label, '0.02 nm');
        });
    }
});

describe('pickRangeScale labels', () => {
    it('sub-1 values trim trailing zeros and the orphan dot', () => {
        assert.equal(pickRangeScale(0.5, 200, 8).label, '0.5 nm');
        assert.equal(pickRangeScale(0.1, 200, 8).label, '0.1 nm');
        assert.equal(pickRangeScale(0.05, 200, 8).label, '0.05 nm');
    });

    it('1 nm and above render as integers', () => {
        assert.equal(pickRangeScale(1.0, 200, 8).label, '1 nm');
        assert.equal(pickRangeScale(5.0, 200, 8).label, '5 nm');
    });
});
