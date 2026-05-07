// Pure decision logic for the chart-tile downshift calibrator.
// Extracted from leafletInterop.js's addChartLayer so the branching
// can be exercised without a Leaflet TileLayer instance, a real map,
// or DOM. The original handler still owns the side-effects (mutating
// layer.options.maxNativeZoom, calling layer.redraw(), the
// console.warn) - this module only computes the decision.
//
// Deferred for discussion (see review SKEP-005 + PARA-004): whether
// the calibrator is the right level of fix at all (the upstream SK
// chart server should publish accurate maxzoom) and whether the
// calibrator should re-arm on recovery rather than only ratcheting
// the cap downward. This file is the seam those discussions will
// cut against - adding a recovery branch or lifting to C# becomes
// a localised change.

/** Threshold: the chart needs to fail at its declared cap this many
 *  times in a row before we believe the metadata is lying. 2 is the
 *  sweet spot - 1 trips on a single transient 404, 3+ delays the
 *  downshift longer than the helm wants to see blank tiles. */
export const CHART_DOWNSHIFT_THRESHOLD = 2;

/**
 * Compute whether a tileerror should drop a chart's maxNativeZoom.
 *
 * @param {object} input
 * @param {number} input.errorZ        Zoom of the failing tile.
 * @param {number} input.currentNative Layer's current maxNativeZoom.
 * @param {number} input.minZoom       Layer's minZoom (floor guard).
 * @param {number} input.priorErrorsAtZ Errors already counted at errorZ
 *                                      against the current cap (before
 *                                      this event).
 * @returns {{ shouldDownshift: boolean, newNative: number,
 *             nextErrorsAtZ: number, reason: string }}
 *   reason is a short tag for diagnostics ("not-at-cap", "below-floor",
 *   "below-threshold", "downshift").
 *
 * The function is total: every input shape (NaN, missing fields,
 * negative zooms) maps to a defensible no-op. Side-effects belong
 * to the caller.
 */
export function decideDownshift(input) {
    const { errorZ, currentNative, minZoom, priorErrorsAtZ } = input;
    // Defensive: a malformed event payload (no coords.z) maps to
    // "do nothing". Leaflet always sets coords on a real tile-fetch
    // failure but the guard is cheap.
    if (typeof errorZ !== 'number' || !Number.isFinite(errorZ)) {
        return { shouldDownshift: false, newNative: currentNative,
                 nextErrorsAtZ: priorErrorsAtZ, reason: 'bad-coords' };
    }
    if (typeof currentNative !== 'number') {
        return { shouldDownshift: false, newNative: currentNative,
                 nextErrorsAtZ: priorErrorsAtZ, reason: 'bad-native' };
    }
    // Sub-cap errors are server hiccups, not metadata lies. Don't
    // count them; the calibrator only acts AT the current cap.
    if (errorZ !== currentNative) {
        return { shouldDownshift: false, newNative: currentNative,
                 nextErrorsAtZ: priorErrorsAtZ, reason: 'not-at-cap' };
    }
    const nextCount = (priorErrorsAtZ ?? 0) + 1;
    if (nextCount < CHART_DOWNSHIFT_THRESHOLD) {
        return { shouldDownshift: false, newNative: currentNative,
                 nextErrorsAtZ: nextCount, reason: 'below-threshold' };
    }
    // Floor guard: dropping the cap below minZoom + 1 would erase
    // the chart entirely. The +1 leaves at least one zoom level for
    // the layer to render at.
    const minZ = (typeof minZoom === 'number' && Number.isFinite(minZoom)) ? minZoom : 1;
    if (currentNative <= minZ + 1) {
        return { shouldDownshift: false, newNative: currentNative,
                 nextErrorsAtZ: nextCount, reason: 'below-floor' };
    }
    // Downshift. The caller resets its per-zoom counter for the OLD
    // cap (so the next downshift, if metadata is off by 2+, starts
    // counting fresh against the NEW cap).
    return { shouldDownshift: true, newNative: currentNative - 1,
             nextErrorsAtZ: 0, reason: 'downshift' };
}
