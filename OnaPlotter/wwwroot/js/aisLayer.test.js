// Tests for aisLayer.js pure helpers.
// Run with: node --test OnaPlotter/wwwroot/js/aisLayer.test.js

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
    unpackLatLonPairs, applyTrailUpdate, acceptsUpdate, toneColor, cpaChipCompact, tipDotParts,
} from './aisLayer.js';

// applyTrailUpdate replays AisTrailBuffer deltas onto the renderer's
// copy; the C# side pins the same contract in
// AisTrailBufferTests.ConsumeDelta_ReplayedOnRendererCopy_MatchesBuffer.
describe('applyTrailUpdate', () => {
    it('a full trail replaces whatever is held', () => {
        const pts = applyTrailUpdate([[9, 9]], [1, 1, 2, 2], [3, 3], 5);
        assert.deepEqual(pts, [[1, 1], [2, 2]]);
    });

    it('trims from the head, then appends', () => {
        const pts = applyTrailUpdate([[1, 1], [2, 2]], undefined, [3, 3], 1);
        assert.deepEqual(pts, [[2, 2], [3, 3]]);
    });

    it('clamps the trim to what is held (points expired before emission)', () => {
        // Held [p1, p2]; C# trimmed p1..p3 (p3 never sent) and appends p4.
        const pts = applyTrailUpdate([[1, 1], [2, 2]], undefined, [4, 4], 3);
        assert.deepEqual(pts, [[4, 4]]);
    });

    it('append only grows the list in order', () => {
        const pts = applyTrailUpdate([[1, 1]], undefined, [2, 2, 3, 3], 0);
        assert.deepEqual(pts, [[1, 1], [2, 2], [3, 3]]);
    });

    it('trim only shrinks', () => {
        assert.deepEqual(applyTrailUpdate([[1, 1], [2, 2]], undefined, undefined, 1), [[2, 2]]);
    });

    it('does not mutate the input', () => {
        const held = [[1, 1], [2, 2]];
        applyTrailUpdate(held, undefined, [3, 3], 1);
        assert.deepEqual(held, [[1, 1], [2, 2]]);
    });
});

describe('acceptsUpdate', () => {
    it('always accepts a full snapshot', () => {
        assert.equal(acceptsUpdate(-1, { baseSeq: -1 }), true);
        assert.equal(acceptsUpdate(7, { baseSeq: -1 }), true);
    });

    it('accepts a delta on the held sequence', () => {
        assert.equal(acceptsUpdate(7, { baseSeq: 7 }), true);
    });

    it('refuses a delta on any other base (state lost or skipped)', () => {
        assert.equal(acceptsUpdate(-1, { baseSeq: 7 }), false);
        assert.equal(acceptsUpdate(6, { baseSeq: 7 }), false);
    });
});

describe('toneColor', () => {
    const palette = { buddy: '#b', danger: '#d', radar: '#r' };
    it('maps theme roles to the live palette', () => {
        assert.equal(toneColor('buddy', '#ship', palette), '#b');
        assert.equal(toneColor('danger', '#ship', palette), '#d');
        assert.equal(toneColor('radar', '#ship', palette), '#r');
    });
    it('ship role uses the vessel colour', () => {
        assert.equal(toneColor('ship', '#ship', palette), '#ship');
    });
});

// unpackLatLonPairs pins the wire-format contract for AIS trail coords:
// AisTrailBuffer.GetCoords emits a flat alternating [lat,lon,...] array;
// the JS-side unpacker must produce Leaflet-shaped [[lat,lon],...] pairs
// in the same order. Any regression here silently corrupts trail
// polylines on the chart.
describe('unpackLatLonPairs', () => {
    it('unpacks a three-point trail into ordered lat/lon tuples', () => {
        const pairs = unpackLatLonPairs([54.0, 11.0, 54.1, 11.1, 54.2, 11.2]);
        assert.deepEqual(pairs, [[54.0, 11.0], [54.1, 11.1], [54.2, 11.2]]);
    });

    it('unpacks the minimum two-point trail', () => {
        const pairs = unpackLatLonPairs([54.0, 11.0, 54.1, 11.1]);
        assert.deepEqual(pairs, [[54.0, 11.0], [54.1, 11.1]]);
    });

    it('returns an empty array for an empty input', () => {
        assert.deepEqual(unpackLatLonPairs([]), []);
    });

    it('silently drops a trailing odd element so callers never observe a half pair', () => {
        // The C# producer guarantees even length; this defends against
        // a future wire-shape bug surfacing as a NaN lon on a fresh point.
        assert.deepEqual(unpackLatLonPairs([54.0, 11.0, 54.1]), [[54.0, 11.0]]);
    });
});

describe('cpaChipCompact', () => {
    it('goes compact under 70 px and back over 90 px', () => {
        assert.equal(cpaChipCompact(69, false), true);
        assert.equal(cpaChipCompact(91, true), false);
    });

    it('holds the previous placement inside the 70-90 px band', () => {
        // A target hovering around 80 px must not flap every push.
        for (const px of [70, 75, 80, 85, 90]) {
            assert.equal(cpaChipCompact(px, true), true, `${px} stays compact`);
            assert.equal(cpaChipCompact(px, false), false, `${px} stays on the line`);
        }
    });

    it('first placement inside the band splits at the midpoint', () => {
        assert.equal(cpaChipCompact(79, null), true);
        assert.equal(cpaChipCompact(81, null), false);
    });

    it('degenerate lengths', () => {
        assert.equal(cpaChipCompact(0, false), true);
        assert.equal(cpaChipCompact(1e6, true), false);
    });
});

describe('tipDotParts', () => {
    it('collects one near-zero-length part per vessel of the colour', () => {
        const parts = tipDotParts([
            { color: '#a', end: [54, 11] },
            { color: '#b', end: [55, 12] },
            { color: '#a', end: [56, 13] },
        ], '#a');
        assert.equal(parts.length, 2);
        assert.deepEqual(parts[0][0], [54, 11]);
        assert.equal(parts[0][1][0], 54);
        assert.ok(parts[0][1][1] > 11 && parts[0][1][1] - 11 < 1e-6);
        assert.deepEqual(parts[1][0], [56, 13]);
    });

    it('is empty when no vessel uses the colour', () => {
        assert.deepEqual(tipDotParts([{ color: '#a', end: [0, 0] }], '#z'), []);
        assert.deepEqual(tipDotParts([], '#a'), []);
    });
});
