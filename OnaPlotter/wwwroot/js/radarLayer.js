// Leaflet layer that paints Signal K Radar v3.1 spoke data onto a
// canvas anchored at own-boat position, scaled to the radar's
// current range.
//
// Dependency note: this module uses the global `L` (Leaflet)
// symbol (L.imageOverlay, L.latLngBounds). Leaflet is loaded as a
// script tag in the host HTML, not as an ES module import, which
// is why there's no `import L from 'leaflet'` here.
//
// Design:
//   * One canvas per radar, sized (2 * maxSpokeLen) square, anchored
//     as a Leaflet ImageOverlay in a circular geographic bounds
//     around own-boat position.
//   * Spoke painting uses a precomputed polar -> pixel LUT (once per
//     radar) indexed by (spokeIndex, rangeCell). Same trick the
//     Freeboard-SK worker uses to avoid per-pixel trig.
//   * North-up: if the spoke has a `bearing` field we paint there;
//     else we rotate `angle` by the own-boat heading.
//   * Pixel bytes are looked up in a Uint8ClampedArray of length 256
//     (one RGBA quad per byte value), built once from the legend.
//     Unknown bytes paint transparent.
//   * Redraw is throttled; we rebuild the ImageOverlay bounds only
//     when range or boat position / heading changes meaningfully.
//
// Scope for MVP:
//   * No Web Worker. If perf requires, switching to a worker with
//     OffscreenCanvas is a local refactor -- the public surface
//     (enableRadar / disableRadar / setBoatState / setRadarRange)
//     stays the same.
//   * Doppler / history / target borders all paint at the legend's
//     configured colours. A future PR can swap to semantic palettes.

/** @typedef {import('./radarProtobuf.js').Spoke} Spoke */

import { decodeRadarMessage } from './radarProtobuf.js';

// Fallback legend for servers that don't ship one in their
// capabilities response. Lifted from Freeboard-SK's default (which
// matches the Navico palette). 0 transparent, 1-4 blue, 5-9 green,
// 10-15 red (weak -> strong returns), 16 target outline (grey),
// 17 doppler approaching (yellow), 18 doppler receding (pale
// blue), 19+ history trails (fading white -> grey).
const DEFAULT_LEGEND_PIXELS = (() => {
    const p = new Array(256).fill(null);
    const set = (i, hex) => { p[i] = hex; };
    set(0, '#00000000');
    for (let i = 1; i <= 4;  i++) set(i, '#0000c8ff');
    for (let i = 5; i <= 9;  i++) set(i, '#00c800ff');
    for (let i = 10; i <= 15; i++) set(i, '#c80000ff');
    set(16, '#c8c8c8ff');         // target border
    set(17, '#c8c800ff');         // doppler approaching
    set(18, '#90d0f0ff');         // doppler receding
    // History / trails fade from white to near-black.
    for (let i = 19; i < 256; i++) {
        const t = Math.min(1, (i - 19) / (256 - 19));
        const v = Math.round(255 * (1 - 0.7 * t));
        set(i, `#${v.toString(16).padStart(2, '0').repeat(3)}ff`);
    }
    return p;
})();

// Per-radar runtime state. Keyed by radar id.
const activeRadars = new Map();

// Shared boat state (lat, lon, headingRad). Updated by setBoatState;
// each active radar reads it at paint / reposition time.
let boatState = { lat: null, lon: null, headingRad: 0 };

/**
 * Enable the radar overlay for one radar. Safe to call twice with
 * the same id (second call is a no-op).
 *
 * @param {object} deps                 Shared Leaflet + map refs from leafletInterop.
 * @param {import('leaflet').Map} deps.map
 * @param {object} cfg
 * @param {string} cfg.radarId
 * @param {string} cfg.spokeDataUrl     Absolute ws:// URL
 * @param {number} cfg.spokesPerRevolution
 * @param {number} cfg.maxSpokeLength
 * @param {number} cfg.range            Current radar range in metres.
 * @param {object} [cfg.legend]         RadarLegend from /capabilities.
 * @param {number} [cfg.opacity]        0..1 overlay opacity; default 0.75.
 *                                      0.75 was chosen as a balance:
 *                                      strong returns are clearly
 *                                      visible against the chart, but
 *                                      underlying coastlines and depth
 *                                      contours remain legible through
 *                                      the sweep. Users who want
 *                                      pure-radar visibility can push
 *                                      to 1.0 via the layers UI.
 */
export function enableRadarOverlay(deps, cfg) {
    if (activeRadars.has(cfg.radarId)) return;
    const rec = new RadarOverlay(deps.map, cfg);
    activeRadars.set(cfg.radarId, rec);
    rec.connect();
}

/** Tear down a radar overlay by id. No-op if not active. */
export function disableRadarOverlay(radarId) {
    const rec = activeRadars.get(radarId);
    if (!rec) return;
    rec.destroy();
    activeRadars.delete(radarId);
}

/** Update the range value (metres) for a radar. Triggers a canvas
 *  reposition on the next animation frame. */
export function setRadarRange(radarId, range) {
    const rec = activeRadars.get(radarId);
    if (!rec) return;
    rec.setRange(range);
}

/** Update own-boat position and heading. All active overlays
 *  reposition. Called from the existing boat-position delta handler
 *  in leafletInterop. */
export function setBoatState(lat, lon, headingRad) {
    boatState = { lat, lon, headingRad: headingRad ?? 0 };
    for (const rec of activeRadars.values()) rec.onBoatStateChanged();
}

/** Debug/tests: expose how many overlays are live. */
export function getActiveRadarCount() { return activeRadars.size; }

// ---------------------------------------------------------------------

class RadarOverlay {
    /**
     * @param {import('leaflet').Map} map
     * @param {object} cfg
     */
    constructor(map, cfg) {
        this.map = map;
        this.radarId = cfg.radarId;
        this.spokeDataUrl = cfg.spokeDataUrl;
        this.spokes = cfg.spokesPerRevolution;
        this.maxSpokeLen = cfg.maxSpokeLength;
        this.range = cfg.range || 1000;
        this.opacity = cfg.opacity ?? 0.75;

        // Canvas is square with side = 2 * maxSpokeLen so the polar
        // origin sits at the centre and the furthest pixel lands on
        // the edge. 2048 -> 4096px = 16 MB ImageData; heavy but
        // manageable. If we hit memory pressure we can drop to
        // maxSpokeLen/2 and accept the resolution loss.
        this.canvasSize = 2 * this.maxSpokeLen;
        this.canvas = document.createElement('canvas');
        this.canvas.width = this.canvasSize;
        this.canvas.height = this.canvasSize;
        this.ctx = this.canvas.getContext('2d', { willReadFrequently: true });
        this.ctx.imageSmoothingEnabled = false;
        // Transparent backdrop: we putImageData so every pixel starts
        // at 0 alpha until a spoke paints over it.
        this.imageData = this.ctx.createImageData(this.canvasSize, this.canvasSize);

        // Precompute polar -> pixel LUT: for each spoke angle + range
        // cell, store the integer (x, y) to paint. Two Int16Arrays;
        // 2 bytes * spokes * maxSpokeLen each.
        //   HALO 31: 2048 * 1024 * 2 = 4 MB per array, 8 MB total.
        this.xLut = new Int16Array(this.spokes * this.maxSpokeLen);
        this.yLut = new Int16Array(this.spokes * this.maxSpokeLen);
        this._computeLuts();

        // Byte -> RGBA LUT. Typed array for cache-friendly lookup
        // inside the paint loop.
        this.byteToRgba = new Uint8ClampedArray(256 * 4);
        this._setLegend(cfg.legend);

        // Leaflet overlay; bounds are set in _repositionOverlay()
        // after we know the boat position. Add once the first frame
        // is ready so Leaflet doesn't place an empty rectangle at
        // (0,0) while we wait for boat state.
        this.overlay = null;

        this.ws = null;
        this.lastRange = 0;
        this.destroyed = false;

        // rAF throttle: overlay image url regeneration is the hot
        // reposition path. Batch to one per frame.
        this._refreshPending = false;
    }

    _computeLuts() {
        const cx = this.maxSpokeLen;
        const cy = this.maxSpokeLen;
        const pixelsPerCell = 1;    // canvas side = 2 * maxSpokeLen, so 1 cell = 1 px
        // Angle 0 = directly North (up on canvas). Spoke `angle` is
        // measured clockwise from bow; bearing is clockwise from
        // true north. We store north-up so bearing maps directly;
        // angle-only gets rotated by heading at paint time.
        const radPerSpoke = (2 * Math.PI) / this.spokes;
        for (let a = 0; a < this.spokes; a++) {
            const theta = a * radPerSpoke - Math.PI / 2;  // 0 spokes = up
            const cos = Math.cos(theta);
            const sin = Math.sin(theta);
            const base = a * this.maxSpokeLen;
            for (let r = 0; r < this.maxSpokeLen; r++) {
                this.xLut[base + r] = Math.round(cx + r * pixelsPerCell * cos);
                this.yLut[base + r] = Math.round(cy + r * pixelsPerCell * sin);
            }
        }
    }

    /** Build byteToRgba from a legend.pixels array of {type, color}.
     *  Unknown indices paint transparent. */
    _setLegend(legend) {
        // Start with fallback palette, then overlay the provider's
        // server-supplied legend if present.
        const fillFromPalette = (palette) => {
            for (let i = 0; i < 256; i++) {
                const hex = palette[i];
                const rgba = hex ? parseHexRgba(hex) : TRANSPARENT;
                this.byteToRgba[i * 4 + 0] = rgba[0];
                this.byteToRgba[i * 4 + 1] = rgba[1];
                this.byteToRgba[i * 4 + 2] = rgba[2];
                this.byteToRgba[i * 4 + 3] = rgba[3];
            }
        };
        fillFromPalette(DEFAULT_LEGEND_PIXELS);
        if (legend && Array.isArray(legend.pixels)) {
            if (legend.pixels.length > 256) {
                // Our byte->RGBA table is sized for the 256 possible
                // byte values. A legend with more entries is a spec
                // violation (a pixel byte can only ever be 0-255);
                // warn so the user notices, then carry on with
                // truncation.
                console.warn('[radar] legend has', legend.pixels.length,
                             'pixels; truncating to 256');
            }
            // Spec's pixels[] is aligned to byte value -- index N
            // corresponds to byte value N.
            for (let i = 0; i < legend.pixels.length && i < 256; i++) {
                const p = legend.pixels[i];
                const rgba = parseLegendColor(p && p.color);
                this.byteToRgba[i * 4 + 0] = rgba[0];
                this.byteToRgba[i * 4 + 1] = rgba[1];
                this.byteToRgba[i * 4 + 2] = rgba[2];
                this.byteToRgba[i * 4 + 3] = rgba[3];
            }
        }
    }

    connect() {
        this._openWebsocket();
    }

    _openWebsocket() {
        if (this.destroyed) return;
        let ws;
        try {
            ws = new WebSocket(this.spokeDataUrl);
        } catch (err) {
            console.warn('[radar] failed to open spoke WS', this.radarId, err);
            this._retryLater();
            return;
        }
        ws.binaryType = 'arraybuffer';
        this.ws = ws;
        ws.addEventListener('message', (ev) => this._onFrame(ev.data));
        ws.addEventListener('close', () => {
            if (this.destroyed) return;
            this.ws = null;
            this._retryLater();
        });
        ws.addEventListener('error', () => {
            // onerror always precedes onclose; nothing to do here but
            // avoid an unhandled-promise noise in the console.
        });
    }

    _retryLater() {
        // 3 s matches Freeboard-SK's retry cadence. Good enough;
        // spoke streams don't usually reject reconnects.
        setTimeout(() => this._openWebsocket(), 3000);
    }

    _onFrame(buffer) {
        if (this.destroyed) return;
        let msg;
        try {
            msg = decodeRadarMessage(new Uint8Array(buffer));
        } catch (err) {
            console.warn('[radar] bad spoke frame', err);
            return;
        }
        const spokes = msg.spokes;
        if (spokes.length === 0) return;

        // If the radar's reported range changed, clear the canvas
        // so we don't keep stale pixels at the old scale. Also
        // drives the overlay bounds recalc.
        const firstRange = spokes[0].range;
        if (firstRange && firstRange !== this.lastRange) {
            this._clearCanvas();
            this.lastRange = firstRange;
            this.range = firstRange;
            this._scheduleReposition();
        }

        // Before painting the new wedge, clear the pixels it covers
        // so a moving-target trail doesn't blur with stale pixels.
        // Cheap: we just zero a sector from first to last spoke in
        // this batch. Freeboard does this with a clip+clear; we do
        // it via the LUT by zeroing every pixel along the wedge.
        //
        // Faster approximation: zero every pixel at the exact spoke
        // angles in this batch. Good enough because spokes usually
        // arrive contiguously, so adjacent spokes' prior content is
        // overwritten in the same batch anyway. If we ever see gappy
        // batches we can widen this to a full angular sweep.
        for (const spoke of spokes) {
            this._clearSpokeIfNeeded(spoke);
            this._paintSpoke(spoke);
        }

        // Flush ImageData once per batch -- not once per spoke.
        this.ctx.putImageData(this.imageData, 0, 0);
        this._scheduleReposition();
    }

    _clearCanvas() {
        this.imageData = this.ctx.createImageData(this.canvasSize, this.canvasSize);
        this.ctx.clearRect(0, 0, this.canvasSize, this.canvasSize);
    }

    _clearSpokeIfNeeded(spoke) {
        // Zero the exact pixels this spoke will paint into so we
        // don't blend atop a stale target.
        const spokeIdx = this._spokeIndex(spoke);
        const base = spokeIdx * this.maxSpokeLen;
        const d = this.imageData.data;
        const w = this.canvasSize;
        const len = Math.min(spoke.data.length, this.maxSpokeLen);
        for (let r = 0; r < len; r++) {
            const x = this.xLut[base + r];
            const y = this.yLut[base + r];
            const p = (y * w + x) * 4;
            d[p + 3] = 0;
        }
    }

    /** @param {Spoke} spoke */
    _paintSpoke(spoke) {
        const spokeIdx = this._spokeIndex(spoke);
        const base = spokeIdx * this.maxSpokeLen;
        const d = this.imageData.data;
        const w = this.canvasSize;
        const lut = this.byteToRgba;
        const len = Math.min(spoke.data.length, this.maxSpokeLen);
        for (let r = 0; r < len; r++) {
            const b = spoke.data[r];
            // Bail early on transparent pixels -- common in open
            // water and saves 4 writes.
            const alpha = lut[b * 4 + 3];
            if (alpha === 0) continue;
            const x = this.xLut[base + r];
            const y = this.yLut[base + r];
            const p = (y * w + x) * 4;
            d[p + 0] = lut[b * 4 + 0];
            d[p + 1] = lut[b * 4 + 1];
            d[p + 2] = lut[b * 4 + 2];
            d[p + 3] = alpha;
        }
    }

    /** Map a spoke's wire angle/bearing onto our canvas's north-up
     *  spoke index. Bearing (if present) wins since it's already
     *  true-north-referenced. Defensive modulo: spec says bearing
     *  is a uint32 so it can't be negative on the wire, but JS
     *  `%` preserves sign for any signed-number corruption coming
     *  from a non-conforming provider. Cheap to guard, expensive
     *  to diagnose (negative index -> out-of-bounds LUT read ->
     *  silently wrong pixels). */
    _spokeIndex(spoke) {
        const n = this.spokes;
        if (spoke.bearing != null) {
            return ((spoke.bearing % n) + n) % n;
        }
        const hdgSpokes = Math.round(boatState.headingRad * n / (2 * Math.PI));
        return (((spoke.angle + hdgSpokes) % n) + n) % n;
    }

    setRange(range) {
        if (!range || range === this.range) return;
        this.range = range;
        this._scheduleReposition();
    }

    onBoatStateChanged() {
        this._scheduleReposition();
    }

    _scheduleReposition() {
        if (this._refreshPending) return;
        this._refreshPending = true;
        requestAnimationFrame(() => {
            this._refreshPending = false;
            this._repositionOverlay();
        });
    }

    _repositionOverlay() {
        if (this.destroyed) return;
        const { lat, lon } = boatState;
        if (lat == null || lon == null) return;

        // Bounding box of a square centred on the boat that just
        // contains the range circle. Side = 2 * range metres.
        // Using a flat-earth deg-per-metre is fine at chartplotter
        // scales (<100 nm). For extreme latitudes we fall back to
        // a cosine-corrected lon step.
        const metresPerDegLat = 111_320;
        const metresPerDegLon = 111_320 * Math.cos(lat * Math.PI / 180) || 1;
        const dLat = this.range / metresPerDegLat;
        const dLon = this.range / metresPerDegLon;

        const bounds = L.latLngBounds(
            [lat - dLat, lon - dLon],
            [lat + dLat, lon + dLon],
        );

        if (!this.overlay) {
            // Convert canvas to data URL lazily -- Leaflet
            // ImageOverlay needs a URL string, so we refresh it on
            // every reposition. For a busy radar this is wasteful
            // (2048x2048 canvas -> dataURL is ~10ms). Acceptable
            // for MVP; optimising with L.canvasOverlay or a custom
            // L.Layer is a follow-up.
            this.overlay = L.imageOverlay(this._canvasUrl(), bounds, {
                opacity: this.opacity,
                interactive: false,
                className: 'radar-overlay',
            }).addTo(this.map);
        } else {
            this.overlay.setBounds(bounds);
            this.overlay.setUrl(this._canvasUrl());
        }
    }

    _canvasUrl() {
        return this.canvas.toDataURL('image/png');
    }

    destroy() {
        this.destroyed = true;
        if (this.ws) {
            try { this.ws.close(); } catch { /* ignore */ }
            this.ws = null;
        }
        if (this.overlay) {
            this.overlay.remove();
            this.overlay = null;
        }
        // Release the LUT memory aggressively -- these can be
        // 4-8 MB per radar.
        this.xLut = null;
        this.yLut = null;
        this.imageData = null;
        this.canvas = null;
    }
}

// --- helpers --------------------------------------------------------

const TRANSPARENT = [0, 0, 0, 0];

function parseHexRgba(hex) {
    if (typeof hex !== 'string' || hex[0] !== '#') return TRANSPARENT;
    if (hex.length === 7) {
        return [
            parseInt(hex.slice(1, 3), 16),
            parseInt(hex.slice(3, 5), 16),
            parseInt(hex.slice(5, 7), 16),
            255,
        ];
    }
    if (hex.length === 9) {
        return [
            parseInt(hex.slice(1, 3), 16),
            parseInt(hex.slice(3, 5), 16),
            parseInt(hex.slice(5, 7), 16),
            parseInt(hex.slice(7, 9), 16),
        ];
    }
    return TRANSPARENT;
}

/** Spec allows two shapes for legend colours: hex string or
 *  {r,g,b,a} object. Tolerate both. */
function parseLegendColor(c) {
    if (!c) return TRANSPARENT;
    if (typeof c === 'string') return parseHexRgba(c);
    if (typeof c === 'object') {
        return [
            c.r | 0,
            c.g | 0,
            c.b | 0,
            c.a == null ? 255 : (c.a | 0),
        ];
    }
    return TRANSPARENT;
}

// Exposed for tests; not part of the public interop API.
export const _internal = { DEFAULT_LEGEND_PIXELS, parseHexRgba, parseLegendColor };
