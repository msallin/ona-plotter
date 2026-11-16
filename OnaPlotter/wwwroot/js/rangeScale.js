// "Nice round" nautical-mile picker for the chart range-scale chip.
//
// Runs on every zoom animation frame, so it lives in JS rather than
// behind an interop round-trip. Pure: no Leaflet, no DOM, so the
// ladder and the width clamp are pinned by rangeScale.test.js.

/**
 * Ladder of "nice round" values (nm), the same steps dedicated
 * chartplotter hardware uses on its range scale.
 */
export const RANGE_SCALE_NM_LADDER = [
    0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 20, 50, 100, 200, 500,
];

/**
 * Picks the largest ladder value at or below the sampled ground
 * distance and the pixel width that value spans. The bar is clamped to
 * the sample width (it may never claim more distance than the sample
 * it represents) and floored at minPx so it stays visible at very wide
 * zooms. A non-positive or non-finite sample renders the floor entry
 * at minPx instead of propagating NaN into the DOM.
 *
 * Example: pickRangeScale(1.7, 200, 8)
 *       -> { niceNm: 1, label: '1 nm', widthPx: 118 }
 *
 * @param {number} sampleNm      ground distance covered by the sample
 * @param {number} sampleWidthPx pixels the sample spans
 * @param {number} minPx         visible floor for the bar
 * @returns {{niceNm: number, label: string, widthPx: number}}
 */
export function pickRangeScale(sampleNm, sampleWidthPx, minPx) {
    const floor = RANGE_SCALE_NM_LADDER[0];
    if (!(sampleNm > 0) || !Number.isFinite(sampleNm)) {
        return { niceNm: floor, label: rangeScaleLabel(floor), widthPx: minPx };
    }
    let nice = floor;
    for (const v of RANGE_SCALE_NM_LADDER) { if (v <= sampleNm) nice = v; }
    const rawWidth = Math.round(sampleWidthPx * (nice / sampleNm));
    const widthPx = Math.min(sampleWidthPx, Math.max(minPx, rawWidth));
    return { niceNm: nice, label: rangeScaleLabel(nice), widthPx };
}

// Sub-1 nm values drop trailing zeros and an orphaned dot ("0.50" reads
// as fake precision; "0.5 nm" is what a chartplotter shows). Values of
// 1 nm and above are integers on the ladder.
// Input: 0.5 -> "0.5 nm"; 0.1 -> "0.1 nm"; 5 -> "5 nm"
function rangeScaleLabel(nm) {
    if (nm >= 1) return `${nm} nm`;
    return `${nm.toFixed(2).replace(/0+$/, '').replace(/\.$/, '')} nm`;
}
