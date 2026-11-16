// Own-track polyline bookkeeping: consecutive same-speed-bucket
// segments share one polyline ("run") that grows with addLatLng, and the
// oldest segments are trimmed past a cap. A day-sail is then a handful
// of polylines instead of one per 5 s segment.
//
// Leaflet-free so trackRuns.test.js can drive it with fake lines: the
// caller supplies createLine(latlngs, bucket) and removeLine(line);
// lines need addLatLng / getLatLngs / setLatLngs.

export class TrackRuns {
    /**
     * @param {(latlngs: number[][], bucket: number) => any} createLine
     * @param {(line: any) => void} removeLine
     * @param {number} maxSegments
     */
    constructor(createLine, removeLine, maxSegments) {
        this.createLine = createLine;
        this.removeLine = removeLine;
        this.maxSegments = maxSegments;
        /** Oldest first: { line, bucket, segments, endLat, endLon }. */
        this.runs = [];
        this.segmentCount = 0;
    }

    /** Forgets every run without touching the lines (caller cleared them). */
    reset() {
        this.runs = [];
        this.segmentCount = 0;
    }

    /** Seeds from C#-grouped runs ({ bucket, coords }); adjacent runs
     *  share a bridge coord. */
    seed(runs) {
        this.reset();
        for (const run of runs) {
            if (run.coords && run.coords.length >= 2) this._start(run.coords, run.bucket);
        }
        this._trim();
    }

    /**
     * Appends one segment prev -> current. Extends the newest run when it
     * continues it in the same bucket; otherwise starts a new run at prev
     * so the trail stays continuous.
     */
    append(lat, lon, bucket, prevLat, prevLon) {
        const last = this.runs[this.runs.length - 1];
        if (last && last.bucket === bucket && last.endLat === prevLat && last.endLon === prevLon) {
            last.line.addLatLng([lat, lon]);
            last.segments++;
            last.endLat = lat;
            last.endLon = lon;
            this.segmentCount++;
        } else {
            this._start([[prevLat, prevLon], [lat, lon]], bucket);
        }
        if (this.segmentCount > this.maxSegments) this._trim();
    }

    _start(latlngs, bucket) {
        const last = latlngs[latlngs.length - 1];
        const run = {
            line: this.createLine(latlngs, bucket),
            bucket,
            segments: latlngs.length - 1,
            endLat: last[0],
            endLon: last[1],
        };
        this.runs.push(run);
        this.segmentCount += run.segments;
    }

    // Drops the oldest segments beyond the cap: whole runs first, then
    // the head of the oldest remaining run.
    _trim() {
        let excess = this.segmentCount - this.maxSegments;
        while (excess > 0 && this.runs.length > 0) {
            const first = this.runs[0];
            if (first.segments <= excess) {
                this.removeLine(first.line);
                this.runs.shift();
                this.segmentCount -= first.segments;
                excess -= first.segments;
            } else {
                const latlngs = first.line.getLatLngs();
                latlngs.splice(0, excess);
                first.line.setLatLngs(latlngs);
                first.segments -= excess;
                this.segmentCount -= excess;
                excess = 0;
            }
        }
    }
}

/**
 * Splits a server track into polylines: one per run of consecutive
 * same-bucket segments, broken wherever a point is excluded by
 * `include` so no chord is drawn across the gap. A segment takes the
 * bucket of its end point; adjacent runs share the bridge point so the
 * line stays visually continuous where only the colour changes.
 *
 * Example (include = lat < 3):
 *   [[0,0,1],[1,1,1],[2,2,2],[3,3,2],[4,4,2],[2,5,2],[1,6,2]]
 *   -> [{ bucket: 1, latlngs: [[0,0],[1,1]] },
 *       { bucket: 2, latlngs: [[1,1],[2,2]] },
 *       { bucket: 2, latlngs: [[2,5],[1,6]] }]
 *
 * @param {number[][]} coords [lat, lon, bucket] samples, oldest first
 * @param {(c: number[]) => boolean} [include] point filter; all when omitted
 * @returns {{ bucket: number, latlngs: number[][] }[]}
 */
export function splitTrackRuns(coords, include = () => true) {
    const out = [];
    let run = null;
    let prev = null;          // previous included point, null after a gap
    for (const c of coords) {
        if (!include(c)) { prev = null; run = null; continue; }
        const ll = [c[0], c[1]];
        if (prev) {
            const bucket = c[2];
            if (!run || run.bucket !== bucket) {
                run = { bucket, latlngs: [prev] };
                out.push(run);
            }
            run.latlngs.push(ll);
        }
        prev = ll;
    }
    return out;
}
