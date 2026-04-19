// Pure logic for the "smart overzoom" chart behaviour. Lives outside
// leafletInterop.js so it can be node-tested without a DOM / Leaflet.
//
// Rules, in one place so they stay consistent:
//   * The chart with the highest native maxZoom gets its maxZoom bumped
//     past native (min 22, or native+4). Lower-native base charts keep
//     their maxZoom pinned to their native, so a detailed harbour chart
//     takes over at high zoom instead of a wide-area chart pixel-
//     stretching over it.
//   * Overlays (OpenSeaMap seamarks etc.) never count in the "top native"
//     race and always get overzoom treatment. A transparent overlay
//     sitting on top of a base chart shouldn't prevent the base chart
//     from stretching past its native limit.

/**
 * Heuristic overlay detection. The SignalK chart schema has no explicit
 * "isOverlay" flag; we pattern-match the identifier and tile URL instead.
 * OpenSeaMap's canonical id is "openseamap" and its URL path segment is
 * "/seamark/". Anything with "seamark" in its URL is also a marine
 * overlay (sector lights, etc.). Base raster charts never match either.
 */
export function isOverlayChart(id, tileUrl) {
    const lc = (s) => (s ?? '').toString().toLowerCase();
    const idl = lc(id);
    const url = lc(tileUrl);
    return idl === 'openseamap'
        || idl.includes('seamark')
        || url.includes('/seamark/');
}

/**
 * Given the native maxZoom for each chart and which ones are overlays,
 * return each chart's EFFECTIVE maxZoom (i.e. the value we should set on
 * the Leaflet layer). Pure; callers apply the result.
 *
 * @param {Map<string, number>} nativeMax - chart id -> native maxZoom
 * @param {Set<string>} overlays - ids of overlay charts (subset of nativeMax keys)
 * @returns {Map<string, number>} - chart id -> effective maxZoom
 */
export function computeOverzoom(nativeMax, overlays) {
    const out = new Map();
    if (nativeMax.size === 0) return out;

    // Pick the top native only from BASE charts. Including overlays here
    // was the regression: an overlay with native 18 pinned every base
    // chart to its own lower native, hiding detailed charts past their
    // limit even though the helmsman had zoomed in further.
    let topNative = 0;
    for (const [id, native] of nativeMax.entries()) {
        if (overlays.has(id)) continue;
        if (native > topNative) topNative = native;
    }

    for (const [id, native] of nativeMax.entries()) {
        // Overlays always get overzoom (transparent, no harm in
        // stretching). Base charts only if they tie for top-native.
        const shouldOverzoom = overlays.has(id) || native >= topNative;
        const effMax = shouldOverzoom ? Math.max(native + 4, 22) : native;
        out.set(id, effMax);
    }
    return out;
}

/**
 * Apply the overzoom decision to a collection of Leaflet chart layers.
 * Separated from `computeOverzoom` so the pure-arithmetic half stays
 * free of side effects, and from `recomputeChartOverzoom` in
 * leafletInterop.js so the side-effect half is testable with stubbed
 * layers. Catches the class of "typed a property the layer dict
 * doesn't have" bugs that crashed addChartLayer in production.
 *
 * @param {string[]} ids - chart ids to update (typically chartLayers.keys())
 * @param {Map<string, number>} nativeMax - chart id -> native maxZoom
 * @param {Set<string>} overlays - ids of overlay charts
 * @param {(id: string) => any} getLayer - fetches the Leaflet layer for an id
 * @param {(id: string, layer: any, effMax: number) => void} setEffMax -
 *        side-effecting callback that applies the new effective maxZoom
 */
export function applyOverzoom(ids, nativeMax, overlays, getLayer, setEffMax) {
    if (!ids || ids.length === 0) return;
    const effective = computeOverzoom(nativeMax, overlays);
    for (const id of ids) {
        const layer = getLayer(id);
        if (!layer) continue;
        const effMax = effective.get(id) ?? (nativeMax.get(id) || 18);
        setEffMax(id, layer, effMax);
    }
}
