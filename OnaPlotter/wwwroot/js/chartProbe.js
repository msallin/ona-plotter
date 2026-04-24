// Pure logic + DOM-free helpers for probing a chart's ACTUAL maxNativeZoom.
//
// Why: SignalK chart metadata commonly lies. A chart declares z13-18 but
// the tile server only has tiles up to z15 or z16. Leaflet honours the
// declared cap and keeps requesting the 404-ing tiles instead of
// overzooming the last-good one. See addChartLayer in leafletInterop.js
// for the caller.
//
// Strategy (Option B from the design discussion): probe at chart-add
// time. Sample 3 representative tile URLs at the declared maxZoom --
// if ANY loads, accept that zoom. If all three 404, step down by one
// and retry. Stop at first success or at minZoom. Set maxNativeZoom
// ONCE and never touch it again -- no tileerror listener, no redraws,
// no cascading overzoom recomputes.
//
// This file is kept pure + injection-driven so chartProbe.test.js can
// exercise the iteration logic without an actual network / DOM. The
// one DOM-touching helper (probeTileWithImage) is exported for the
// production caller but not used in tests.

/**
 * lat/lon -> XYZ tile coordinates at a given zoom. Standard Web
 * Mercator (EPSG:3857) projection as used by all major tile servers
 * including OpenSeaMap, OSM, and most SignalK chart providers.
 *
 * @param {number} lat  latitude in degrees
 * @param {number} lon  longitude in degrees
 * @param {number} z    zoom level (integer)
 * @returns {[number, number]} [x, y]
 */
export function latLonToTile(lat, lon, z) {
    const n = Math.pow(2, z);
    const x = Math.floor((lon + 180) / 360 * n);
    const latRad = lat * Math.PI / 180;
    const y = Math.floor(
        (1 - Math.log(Math.tan(latRad) + 1 / Math.cos(latRad)) / Math.PI) / 2 * n);
    // Clamp; the far-north/south formula can drift slightly past [0, n-1]
    // at extreme latitudes, producing invalid tile URLs.
    const clamped = (v) => Math.max(0, Math.min(n - 1, v));
    return [clamped(x), clamped(y)];
}

/**
 * Substitute the standard Leaflet tile URL placeholders. Supports
 * {z}/{x}/{y} and {s} (subdomain sharding -- fills with "a" since we
 * only probe once per zoom and don't need actual load distribution).
 * Leaves unknown placeholders untouched so odd templates don't break.
 */
export function fillTileUrl(template, z, x, y) {
    return template
        .replace('{s}', 'a')
        .replace('{z}', String(z))
        .replace('{x}', String(x))
        .replace('{y}', String(y))
        .replace('{r}', '');   // "retina" suffix; empty is the 1x variant
}

/**
 * Probe a tile URL by loading it as an <img>. Returns true on load,
 * false on error / timeout. Uses <img> (not fetch) because:
 *   - CORS headers are not required for detecting load/error
 *   - Many chart servers reject OPTIONS preflight on HEAD
 *   - It's exactly how Leaflet itself fetches tiles, so any server
 *     that would serve Leaflet will serve a probe
 *
 * Timeout is generous (6 s) because a first-ever fetch on mobile can
 * be slow; we'd rather wait than wrongly assume "404" on a cold cache.
 *
 * Pass the real URL (already substituted). Exported so the production
 * caller can inject it into iterateProbe; tests inject a fake instead.
 */
export function probeTileWithImage(url, timeoutMs = 6000) {
    return new Promise((resolve) => {
        let settled = false;
        const img = new Image();
        const t = setTimeout(() => {
            if (!settled) { settled = true; resolve(false); }
        }, timeoutMs);
        img.onload = () => { if (!settled) { settled = true; clearTimeout(t); resolve(true); } };
        img.onerror = () => { if (!settled) { settled = true; clearTimeout(t); resolve(false); } };
        img.src = url;
    });
}

/**
 * Discover the actual maxNativeZoom for a chart by probing tiles at
 * representative lat/lon samples across its bounds. Starts from the
 * declared max and steps down until a probe hits. Stops at minZoom
 * or maxStepdown, whichever is reached first.
 *
 * Callers inject the probe function so tests can bypass the network
 * and the production path can use probeTileWithImage.
 *
 * @param {object} args
 * @param {string}   args.tileUrl       Leaflet URL template with {z}/{x}/{y}.
 * @param {[number,number,number,number]|null} args.bounds  [west, south, east, north] in degrees.
 * @param {number}   args.declaredMax   maxZoom the metadata claims.
 * @param {number}   args.minZoom       floor at which to give up.
 * @param {number}   [args.maxStepdown] maximum zooms to drop from declared (default 6).
 * @param {(url:string) => Promise<boolean>} args.probe  tile-exists probe.
 * @returns {Promise<number>}  the discovered maxNativeZoom (always
 *   >= minZoom; defaults to declaredMax if bounds absent).
 */
export async function discoverMaxNativeZoom({
    tileUrl, bounds, declaredMax, minZoom,
    maxStepdown = 6, probe,
}) {
    // Without bounds we can't pick a representative tile; trust the
    // declared value. Happens with legacy chart metadata.
    if (!bounds || bounds.length !== 4) return declaredMax;
    const [west, south, east, north] = bounds;
    const samples = [
        // Centre + two interior quarter points. All three inside bounds
        // so there's a reasonable chance the chart actually covers them
        // (tile servers often have holes near the edges).
        [(south + north) / 2, (east + west) / 2],
        [south + (north - south) * 0.3, west + (east - west) * 0.3],
        [south + (north - south) * 0.7, west + (east - west) * 0.7],
    ];
    const floor = Math.max(minZoom, declaredMax - maxStepdown);
    for (let z = declaredMax; z >= floor; z--) {
        const urls = samples.map(([lat, lon]) => {
            const [x, y] = latLonToTile(lat, lon, z);
            return fillTileUrl(tileUrl, z, x, y);
        });
        const results = await Promise.all(urls.map(probe));
        if (results.some(Boolean)) return z;
    }
    // Everything failed; fall back to minZoom so the chart at least
    // tries something instead of disappearing entirely. The overzoom
    // stretch still fills the viewport.
    return minZoom;
}
