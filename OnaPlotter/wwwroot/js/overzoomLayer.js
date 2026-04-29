// Chart upscale ("overzoom") decorator.
//
// Lets the helm zoom past a chart's native max by GPU-upscaling
// tiles at the native cap. The decorator is a higher-order function
// that mutates an options bag before L.tileLayer(url, opts) is
// called -- no Leaflet subclass, no runtime probe, no calibrator.
// Removable in one go: drop this file + the addChartLayer call site
// + the Settings flag and the feature is gone.
//
// Why no probe: see docs/design/overzoom.md (and the design draft).
// The previous implementation tried to detect missing tiles at
// runtime via 404 / 200-empty-PNG / redirect heuristics. Each
// SignalK chart-plugin variant behaves differently. The probe
// jittered, the calibrator settled at unstable values, and the
// helm saw tiles popping in and out. v2 trusts the metadata: if
// MBTiles says maxzoom 18, we believe it. When metadata lies,
// the helm sees blank tiles past the real cap (with OSM showing
// through underneath via the existing fallback). That's an honest
// signal -- not a regression hidden behind silently-failing logic.

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
        return { ...opts };
    }
    const native = opts.maxNativeZoom ?? opts.maxZoom ?? 18;
    return {
        ...opts,
        maxNativeZoom: native,
        maxZoom: native + lv,
        // Tag the layer so the dev-section diagnostics + tests can
        // distinguish upscaled chart layers from bare ones without
        // groveling through option values.
        _chartUpscaleLevels: lv,
    };
}
