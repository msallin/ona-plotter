// Tests for trackRuns.js (own-track run polylines).
// Run with: node --test OnaPlotter/wwwroot/js/trackRuns.test.js

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { TrackRuns, splitTrackRuns } from './trackRuns.js';

// Minimal stand-in for an L.polyline.
class FakeLine {
    constructor(latlngs, bucket) { this.latlngs = latlngs.map(p => [...p]); this.bucket = bucket; }
    addLatLng(p) { this.latlngs.push(p); }
    getLatLngs() { return this.latlngs; }
    setLatLngs(l) { this.latlngs = l; }
}

function make(max = 1000) {
    const lines = [];
    const tr = new TrackRuns(
        (latlngs, bucket) => { const l = new FakeLine(latlngs, bucket); lines.push(l); return l; },
        (line) => { const i = lines.indexOf(line); if (i >= 0) lines.splice(i, 1); },
        max);
    return { tr, lines };
}

describe('TrackRuns.append', () => {
    it('same bucket, contiguous segments extend one polyline', () => {
        const { tr, lines } = make();
        tr.append(1, 1, 2, 0, 0);
        tr.append(2, 2, 2, 1, 1);
        tr.append(3, 3, 2, 2, 2);
        assert.equal(lines.length, 1);
        assert.deepEqual(lines[0].latlngs, [[0, 0], [1, 1], [2, 2], [3, 3]]);
        assert.equal(tr.segmentCount, 3);
    });

    it('a bucket change starts a new run at the previous fix', () => {
        const { tr, lines } = make();
        tr.append(1, 1, 2, 0, 0);
        tr.append(2, 2, 4, 1, 1);
        assert.equal(lines.length, 2);
        assert.deepEqual(lines[1].latlngs, [[1, 1], [2, 2]]);
        assert.equal(lines[1].bucket, 4);
    });

    it('a gap (prev not the run end) starts a new run even in the same bucket', () => {
        // Local trail resumes after suppression from a fresh prev fix.
        const { tr, lines } = make();
        tr.append(1, 1, 2, 0, 0);
        tr.append(6, 6, 2, 5, 5);
        assert.equal(lines.length, 2);
    });
});

describe('TrackRuns cap', () => {
    it('trims the head of the oldest run past the cap', () => {
        const { tr, lines } = make(3);
        tr.append(1, 1, 0, 0, 0);
        tr.append(2, 2, 0, 1, 1);
        tr.append(3, 3, 0, 2, 2);
        tr.append(4, 4, 0, 3, 3);
        assert.equal(tr.segmentCount, 3);
        assert.deepEqual(lines[0].latlngs, [[1, 1], [2, 2], [3, 3], [4, 4]]);
    });

    it('drops whole runs that fall entirely past the cap', () => {
        const { tr, lines } = make(2);
        tr.append(1, 1, 0, 0, 0);          // run A: 1 segment
        tr.append(2, 2, 3, 1, 1);          // run B: 1 segment
        tr.append(3, 3, 5, 2, 2);          // run C: 1 segment -> A dropped
        assert.equal(lines.length, 2);
        assert.deepEqual(lines.map(l => l.bucket), [3, 5]);
        assert.equal(tr.segmentCount, 2);
    });
});

describe('TrackRuns.seed', () => {
    it('creates one line per drawable run and counts its segments', () => {
        const { tr, lines } = make();
        tr.seed([
            { bucket: 1, coords: [[0, 0], [1, 1], [2, 2]] },
            { bucket: 2, coords: [[2, 2]] },             // not drawable
            { bucket: 3, coords: [[2, 2], [3, 3]] },
        ]);
        assert.equal(lines.length, 2);
        assert.equal(tr.segmentCount, 3);
    });

    it('a live append continues the last seeded run', () => {
        const { tr, lines } = make();
        tr.seed([{ bucket: 1, coords: [[0, 0], [1, 1]] }]);
        tr.append(2, 2, 1, 1, 1);
        assert.equal(lines.length, 1);
        assert.equal(tr.segmentCount, 2);
    });

    it('seeding past the cap trims immediately', () => {
        const { tr } = make(2);
        tr.seed([{ bucket: 1, coords: [[0, 0], [1, 1], [2, 2], [3, 3]] }]);
        assert.equal(tr.segmentCount, 2);
    });
});

describe('splitTrackRuns', () => {
    it('groups consecutive same-bucket segments with a bridge point', () => {
        const runs = splitTrackRuns([[0, 0, 1], [1, 1, 1], [2, 2, 2], [3, 3, 2]]);
        assert.deepEqual(runs, [
            { bucket: 1, latlngs: [[0, 0], [1, 1]] },
            { bucket: 2, latlngs: [[1, 1], [2, 2], [3, 3]] },
        ]);
    });

    it('breaks the line at excluded points instead of drawing a chord', () => {
        // Track leaves the padded view and comes back: the out-of-view
        // stretch must not be replaced by a straight line across it.
        const runs = splitTrackRuns(
            [[0, 0, 1], [1, 1, 1], [2, 2, 2], [3, 3, 2], [4, 4, 2], [2, 5, 2], [1, 6, 2]],
            c => c[0] < 3);
        assert.deepEqual(runs, [
            { bucket: 1, latlngs: [[0, 0], [1, 1]] },
            { bucket: 2, latlngs: [[1, 1], [2, 2]] },
            { bucket: 2, latlngs: [[2, 5], [1, 6]] },
        ]);
    });

    it('drops isolated single points', () => {
        assert.deepEqual(splitTrackRuns([[0, 0, 1], [5, 5, 1], [0, 1, 1]], c => c[0] < 3), []);
    });

    it('handles empty and one-point input', () => {
        assert.deepEqual(splitTrackRuns([]), []);
        assert.deepEqual(splitTrackRuns([[0, 0, 1]]), []);
    });
});
