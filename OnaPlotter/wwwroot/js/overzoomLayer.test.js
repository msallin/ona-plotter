// Tests for overzoomLayer.js (the chart-upscale decorator).
// Run with: node --test OnaPlotter/wwwroot/js/overzoomLayer.test.js
//
// Existence rationale: the decorator is the only piece of decision
// logic in the upscale feature that lives in JS rather than C#
// (clamp `levels|0` to 0..3, branch on lv === 0, fallback chain
// `maxNativeZoom ?? maxZoom ?? 18`). Without a test, a regression
// that drops the lower clamp, mutates the input, or changes the
// default-18 fallback ships green -- C# can't see it. The project
// rule (CLAUDE.md) says decisions in C# with C# tests; until the
// decorator is lifted, this is the next-best protection.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { withOverzoom } from './overzoomLayer.js';

describe('withOverzoom: zero-levels no-op path', () => {
    it('returns a structural copy when levels = 0', () => {
        const opts = { maxNativeZoom: 18, foo: 'bar' };
        const result = withOverzoom(opts, 0);
        assert.deepEqual(result, opts);
        // Must be a new object so callers can mutate the result safely.
        assert.notStrictEqual(result, opts);
    });

    it('does not add an internal _chartUpscaleLevels tag', () => {
        // The tag was dropped (zero readers) -- a future re-introduction
        // for diagnostics needs to be a deliberate code change with
        // a real consumer, not a quiet sneak-back.
        const result = withOverzoom({ maxNativeZoom: 18 }, 0);
        assert.equal(Object.prototype.hasOwnProperty.call(result, '_chartUpscaleLevels'), false);
    });

    it('treats negative levels as 0 (clamp lower) -> structural copy only', () => {
        const opts = { maxNativeZoom: 18, opacity: 0.8 };
        const result = withOverzoom(opts, -5);
        // No-op: matches the lv === 0 path. maxZoom is NOT added; the
        // call site sets it before invoking, so the structural copy
        // already carries everything Leaflet needs.
        assert.deepEqual(result, opts);
    });

    it('treats NaN levels as 0 -> structural copy only', () => {
        // `NaN | 0 === 0` is the load-bearing JS coercion. A future
        // refactor that switches to `Math.trunc(levels)` (which
        // returns NaN for NaN) would change behaviour without this test.
        const opts = { maxNativeZoom: 18 };
        assert.deepEqual(withOverzoom(opts, NaN), opts);
    });

    it('treats null / undefined levels as 0 -> structural copy only', () => {
        const opts = { maxNativeZoom: 18 };
        assert.deepEqual(withOverzoom(opts, null), opts);
        assert.deepEqual(withOverzoom(opts, undefined), opts);
    });
});

describe('withOverzoom: levels > 0 path', () => {
    it('bumps maxZoom by levels and preserves maxNativeZoom', () => {
        const result = withOverzoom({ maxNativeZoom: 15 }, 2);
        assert.equal(result.maxNativeZoom, 15);
        assert.equal(result.maxZoom, 17);
    });

    it('clamps levels to 3 (upper)', () => {
        const result = withOverzoom({ maxNativeZoom: 15 }, 99);
        assert.equal(result.maxZoom, 18);   // 15 + 3, not 15 + 99
    });

    it('clamps fractional levels via |0 truncation toward zero', () => {
        // `2.7 | 0 === 2`, not 3 -- pin the truncation semantic so a
        // refactor to Math.round / Math.floor surfaces visibly.
        const result = withOverzoom({ maxNativeZoom: 18 }, 2.7);
        assert.equal(result.maxZoom, 20);
    });

    it('coerces string-shaped levels via |0', () => {
        // `"2" | 0 === 2`. A future refactor that stops trusting JS
        // coercion would need to update the C#-side guarantee that
        // levels is always a real int.
        const result = withOverzoom({ maxNativeZoom: 18 }, "2");
        assert.equal(result.maxZoom, 20);
    });
});

describe('withOverzoom: maxNativeZoom fallback chain', () => {
    it('falls back to opts.maxZoom when maxNativeZoom is absent', () => {
        // Some callers might set only maxZoom; the decorator should
        // promote that to maxNativeZoom rather than pegging at 18.
        const result = withOverzoom({ maxZoom: 15 }, 2);
        assert.equal(result.maxNativeZoom, 15);
        assert.equal(result.maxZoom, 17);
    });

    it('falls back to 18 when both are absent', () => {
        const result = withOverzoom({}, 2);
        assert.equal(result.maxNativeZoom, 18);
        assert.equal(result.maxZoom, 20);
    });

    it('preserves other unrelated options', () => {
        const result = withOverzoom(
            { maxNativeZoom: 18, opacity: 0.8, attribution: '<a>foo</a>' }, 2);
        assert.equal(result.opacity, 0.8);
        assert.equal(result.attribution, '<a>foo</a>');
    });
});

describe('withOverzoom: input-immutability contract', () => {
    it('does not mutate the input options bag', () => {
        const opts = { maxNativeZoom: 15, opacity: 0.8 };
        const before = JSON.stringify(opts);
        withOverzoom(opts, 3);
        assert.equal(JSON.stringify(opts), before);
    });

    it('returns a fresh object (not a reference share) even when lv = 0', () => {
        const opts = { maxNativeZoom: 18 };
        const result = withOverzoom(opts, 0);
        assert.notStrictEqual(result, opts);
    });
});
