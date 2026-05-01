// Tests for chartDownshift.js (decision logic only).
// Run with: node --test OnaPlotter/wwwroot/js/chartDownshift.test.js
//
// Pinning rationale: a regression that off-by-ones the threshold,
// drops the floor guard, or counts sub-cap errors would silently
// degrade -- the only on-helm signal is a console.warn. The pure
// function is the smallest seam that lets the branches fail loud.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { decideDownshift, CHART_DOWNSHIFT_THRESHOLD } from './chartDownshift.js';

describe('decideDownshift: threshold semantics', () => {
    it('does not downshift on the first error at the cap', () => {
        const r = decideDownshift({
            errorZ: 18, currentNative: 18, minZoom: 1, priorErrorsAtZ: 0
        });
        assert.equal(r.shouldDownshift, false);
        assert.equal(r.nextErrorsAtZ, 1);
        assert.equal(r.reason, 'below-threshold');
        assert.equal(r.newNative, 18);
    });

    it('downshifts on the threshold-th error at the cap', () => {
        const r = decideDownshift({
            errorZ: 18, currentNative: 18, minZoom: 1,
            priorErrorsAtZ: CHART_DOWNSHIFT_THRESHOLD - 1
        });
        assert.equal(r.shouldDownshift, true);
        assert.equal(r.newNative, 17);
        assert.equal(r.nextErrorsAtZ, 0);
        assert.equal(r.reason, 'downshift');
    });

    it('threshold is 2 (pinned constant)', () => {
        // The threshold is calibrated against helm experience: 1 trips
        // on a single transient 404; 3+ delays the downshift longer
        // than the helm wants to see blank tiles. Pin so a refactor
        // doesn't silently shift it.
        assert.equal(CHART_DOWNSHIFT_THRESHOLD, 2);
    });
});

describe('decideDownshift: not-at-cap branch', () => {
    it('ignores errors below the current cap', () => {
        const r = decideDownshift({
            errorZ: 17, currentNative: 18, minZoom: 1, priorErrorsAtZ: 5
        });
        assert.equal(r.shouldDownshift, false);
        assert.equal(r.reason, 'not-at-cap');
        // Counter is unchanged when error is sub-cap.
        assert.equal(r.nextErrorsAtZ, 5);
    });

    it('ignores errors above the current cap (paranoid)', () => {
        // Shouldn't be possible (the layer caps requests at native)
        // but the guard handles it anyway.
        const r = decideDownshift({
            errorZ: 19, currentNative: 18, minZoom: 1, priorErrorsAtZ: 5
        });
        assert.equal(r.shouldDownshift, false);
        assert.equal(r.reason, 'not-at-cap');
    });
});

describe('decideDownshift: floor guard', () => {
    it('refuses to drop below minZoom + 1', () => {
        // Pin: dropping further would erase the chart entirely.
        const r = decideDownshift({
            errorZ: 2, currentNative: 2, minZoom: 1,
            priorErrorsAtZ: CHART_DOWNSHIFT_THRESHOLD - 1
        });
        assert.equal(r.shouldDownshift, false);
        assert.equal(r.reason, 'below-floor');
        assert.equal(r.newNative, 2);
    });

    it('allows the last legal downshift exactly at minZoom + 2 -> minZoom + 1', () => {
        const r = decideDownshift({
            errorZ: 3, currentNative: 3, minZoom: 1,
            priorErrorsAtZ: CHART_DOWNSHIFT_THRESHOLD - 1
        });
        assert.equal(r.shouldDownshift, true);
        assert.equal(r.newNative, 2);
    });

    it('treats missing minZoom as 1 (default)', () => {
        const r = decideDownshift({
            errorZ: 2, currentNative: 2,
            priorErrorsAtZ: CHART_DOWNSHIFT_THRESHOLD - 1
        });
        assert.equal(r.shouldDownshift, false);
        assert.equal(r.reason, 'below-floor');
    });
});

describe('decideDownshift: defensive shape handling', () => {
    it('returns no-op when errorZ is not a number', () => {
        const r = decideDownshift({
            errorZ: undefined, currentNative: 18, minZoom: 1, priorErrorsAtZ: 0
        });
        assert.equal(r.shouldDownshift, false);
        assert.equal(r.reason, 'bad-coords');
    });

    it('returns no-op when errorZ is NaN', () => {
        const r = decideDownshift({
            errorZ: NaN, currentNative: 18, minZoom: 1, priorErrorsAtZ: 0
        });
        assert.equal(r.shouldDownshift, false);
        assert.equal(r.reason, 'bad-coords');
    });

    it('returns no-op when currentNative is missing', () => {
        const r = decideDownshift({
            errorZ: 18, currentNative: undefined, minZoom: 1, priorErrorsAtZ: 0
        });
        assert.equal(r.shouldDownshift, false);
        assert.equal(r.reason, 'bad-native');
    });

    it('treats missing priorErrorsAtZ as 0', () => {
        const r = decideDownshift({
            errorZ: 18, currentNative: 18, minZoom: 1
        });
        assert.equal(r.nextErrorsAtZ, 1);
        assert.equal(r.shouldDownshift, false);
    });
});

describe('decideDownshift: multi-step sequence', () => {
    it('chart claiming maxzoom 18 actually capped at 16 settles via two downshifts', () => {
        // Realistic regression: MBTiles header lies by 2. Helm zooms
        // to 18, sees blank, calibrator drops to 17 after 2 errors,
        // then to 16 after 2 more. After that, errors stop because
        // 16 has tiles.
        let native = 18;
        let countAtCap = 0;

        // First two errors at z=18.
        for (let i = 0; i < CHART_DOWNSHIFT_THRESHOLD; i++) {
            const r = decideDownshift({
                errorZ: 18, currentNative: native, minZoom: 1,
                priorErrorsAtZ: countAtCap
            });
            if (r.shouldDownshift) {
                native = r.newNative;
                countAtCap = 0;
            } else {
                countAtCap = r.nextErrorsAtZ;
            }
        }
        assert.equal(native, 17);
        assert.equal(countAtCap, 0);

        // Two errors at the new cap z=17.
        for (let i = 0; i < CHART_DOWNSHIFT_THRESHOLD; i++) {
            const r = decideDownshift({
                errorZ: 17, currentNative: native, minZoom: 1,
                priorErrorsAtZ: countAtCap
            });
            if (r.shouldDownshift) {
                native = r.newNative;
                countAtCap = 0;
            } else {
                countAtCap = r.nextErrorsAtZ;
            }
        }
        assert.equal(native, 16);
    });
});
