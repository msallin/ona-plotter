// Chart upscale ("overzoom") decorator.
//
// Lets the helm zoom past a chart's native max by GPU-upscaling
// tiles at the native cap. The decorator is a higher-order function
// that returns a new options bag for L.tileLayer(url, opts) -- no
// Leaflet subclass, no runtime probe.
//
// Removable in one go: drop this file + the addChartLayer call site
// + the Settings flag and the feature is gone.
//
// History: see docs/design/overzoom.md. v1 detected missing tiles at
// runtime via 404 / 200-empty-PNG / redirect heuristics; the probe
// jittered, settled at unstable values, and the helm saw tiles
// popping in and out. v2 trusted the metadata but ran a tileerror-
// driven downshift calibrator to compensate when chart servers over-
// declared maxzoom -- which broke MBTiles files that legitimately
// have *holes* at high zoom (regional coverage variation: z18 in
// harbours, z14 offshore). v3 (current) drops the calibrator: tiles
// past the chart's declared native maxzoom are not fetched, holes
// at the current zoom 404 silently and the basemap below shows
// through. Honest signal where the data ends. The decorator below
// is what makes a low declared maxzoom still readable at the helm's
// view zoom (GPU-upscaled tile from native cap).

/**
 * Wrap a Leaflet TileLayer options bag for chart upscaling.
 * `levels` is the number of zoom steps past native to allow before
 * tiles go blank. 0 means no-op (returns a structural copy).
 *
 * Reads `opts.maxNativeZoom` (already set by the caller from the
 * MBTiles metadata) and bumps `opts.maxZoom` to native + levels so
 * Leaflet upscales the last fetched tile rather than fetching
 * non-existent ones at higher zooms. `maxNativeZoom` itself is left
 * alone, which caps the actual fetch budget at the chart's real
 * tile pyramid -- a regression that drops the cap and triggers
 * 16x more requests is exactly what we don't want, so the wrap
 * preserves the budget explicitly.
 *
 * Returns a new options object; the input is not mutated.
 */
export function withOverzoom(opts, levels) {
    const lv = Math.max(0, Math.min(3, levels | 0));
    if (lv === 0) {
        // Identity: a structural copy keeps callers safe from later
        // mutation of the returned object without paying the bump-
        // by-zero assignment.
        return { ...opts };
    }
    const native = opts.maxNativeZoom ?? opts.maxZoom ?? 18;
    return {
        ...opts,
        maxNativeZoom: native,
        maxZoom: native + lv,
    };
}
