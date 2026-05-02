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
// Why no probe: see docs/design/overzoom.md (and the design draft).
// The previous implementation tried to detect missing tiles at
// runtime via 404 / 200-empty-PNG / redirect heuristics. Each
// SignalK chart-plugin variant behaves differently. The probe
// jittered, the calibrator settled at unstable values, and the
// helm saw tiles popping in and out. v2 trusts the metadata: if
// MBTiles says maxzoom 18, we believe it. The downshift calibrator
// in leafletInterop.js (driven by bonafide `tileerror` 404s, only
// at the current cap) handles the metadata-lies-about-maxzoom
// case without re-introducing the probe instability.

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
    const lv = Math.max(0, Math.min(5, levels | 0));
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
