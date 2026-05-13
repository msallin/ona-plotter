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
//   * One canvas per radar, sized maxSpokeLen square, attached
//     directly to Leaflet's overlay pane via a custom L.Layer (see
//     CanvasGeoLayer at the bottom of this file). The canvas IS the
//     displayed pixels; no toDataURL / blob / imageOverlay dance,
//     which would cost 10-50 ms per reposition on the main thread.
//     Each pixel covers two range cells (pixelsPerCell = 0.5): the
//     overlay is CSS-scaled down to roughly screen pixels anyway,
//     so finer polar resolution is invisible and the smaller buffer
//     (4 MB at typical maxSpokeLen=1024 vs 16 MB for 1:1) lets
//     multi-radar setups stay inside compositor memory.
//   * Spoke painting uses a precomputed polar -> pixel LUT (once per
//     radar) indexed by (spokeIndex, rangeCell). Avoids per-pixel
//     trig on every spoke.
//   * putImageData uploads are rAF-coalesced: the spoke painter
//     mutates the ImageData buffer immediately, but the GPU upload
//     of the accumulated dirty rect is deferred to the next animation
//     frame. Decouples upload traffic from the WS message rate
//     (~50 Hz on a typical radar) and caps it at the display refresh.
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
//     OffscreenCanvas is a local refactor - the public surface
//     (enableRadar / disableRadar / setBoatState / setRadarRange)
//     stays the same.
//   * Doppler / history / target borders all paint at the legend's
//     configured colours. A future PR can swap to semantic palettes.

/** @typedef {import('./radarProtobuf.js').Spoke} Spoke */

import { decodeRadarMessage } from './radarProtobuf.js';
import { rangeRingLabel } from './format.js';

// Fallback legend for servers that don't ship one in their
// capabilities response. Matches a typical recreational radar
// palette. 0 transparent, 1-4 blue, 5-9 green, 10-15 red (weak ->
// strong returns), 16 target
// outline (grey), 17 doppler approaching (yellow), 18 doppler
// receding (pale blue), 19+ history trails (fading white -> grey).
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
//
// CRITICAL: `headingRad` here must be TRUE-NORTH (not magnetic). The
// canvas is anchored to a true-north chart, and `_spokeIndex` adds
// this value to the spoke's bow-relative `angle` to produce the
// canvas index. Feeding a magnetic heading rotates every spoke by
// the local magnetic variation. The C# side passes `HeadingTrueResolved`
// (see NavigationData) rather than the helm's display-preference
// `Heading`, so the radar stays aligned regardless of whether the
// helm reads HUD values in true or magnetic.
let boatState = { lat: null, lon: null, headingRad: 0 };

// Range-ring overlay config. Helm-toggleable: when enabled, every
// active radar overlay paints `count` concentric circles centred on
// own boat at evenly-spaced fractions of the radar's current range
// (1/N, 2/N, ..., N/N). Default-on, default 4 rings; the C# side
// pushes the helm-set values via setRangeRingsConfig at init + on
// settings change. When disabled the per-overlay rings group is
// torn down on the next refresh.
let ringsEnabled = true;
let ringsCount = 4;

/**
 * Update the global range-rings configuration. Refreshes every
 * active radar overlay so the helm sees the new rings (or their
 * absence) on the next render tick.
 */
export function setRangeRingsConfig(enabled, count) {
    ringsEnabled = !!enabled;
    // Clamp to a sane range - below 1 there's nothing to draw, above
    // 8 the chart turns into a bullseye. The Settings UI offers 1-6
    // anyway.
    const n = Number(count);
    ringsCount = Number.isFinite(n) ? Math.max(1, Math.min(8, Math.round(n))) : 4;
    for (const rec of activeRadars.values()) {
        // Defensive: skip records whose Leaflet map has been removed
        // (e.g. tearDownAllRadarOverlays didn't run before initMap on a
        // navigate-away-and-back). Adding a vector layer to a dead map
        // crashes deep in Leaflet's getRenderer with
        // "Cannot read properties of undefined (reading 'appendChild')"
        // because _panes was wiped by map.remove().
        if (!rec.map || typeof rec.map.getPanes !== 'function') continue;
        const panes = rec.map.getPanes();
        if (!panes || !panes.overlayPane) continue;
        try { rec.refreshRangeRings(); }
        catch (e) {
            // Last-resort: a refresh that throws shouldn't break the
            // helm's chart. Log + drop the dead record so subsequent
            // iterations skip it.
            console.warn('[radar] refreshRangeRings threw; dropping overlay', e);
            try { rec.destroy(); } catch (_) { /* already gone */ }
        }
    }
}

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
 * @param {boolean} [cfg.useWireBearing] Default false. When true, the
 *                                      overlay trusts the wire spoke's
 *                                      optional `bearing` field as
 *                                      true-north. When false (default)
 *                                      bearing is ignored and the spoke
 *                                      index is always `angle + heading`
 *                                      composed at paint time. Default
 *                                      is off because at least one live
 *                                      provider (Mayara) fills bearing
 *                                      with the radar's internal HS-
 *                                      corrected value rather than
 *                                      true-north, which paints spokes
 *                                      bow-up regardless of heading
 *                                      whenever the radar has no HS
 *                                      sensor wired in (the common
 *                                      case on a recreational install).
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

/**
 * Tear down EVERY active radar overlay. Called from
 * leafletInterop.initMap before it calls map.remove() so that
 * stale records pointing at a destroyed Leaflet map don't survive
 * a navigate-away-and-back. Without this, a follow-up
 * setRangeRingsConfig() call iterates active records, calls
 * rec.refreshRangeRings(), and tries to add a layer to the OLD
 * (already-removed) map - whose _panes.overlayPane is now undefined,
 * surfacing as "Cannot read properties of undefined (reading
 * 'appendChild')" deep in Leaflet's getRenderer + crashing the
 * Blazor renderer on the way out.
 *
 * destroy() is best-effort: each record's teardown is wrapped so
 * one failed cleanup can't strand the rest.
 */
export function tearDownAllRadarOverlays() {
    for (const rec of activeRadars.values()) {
        try { rec.destroy(); } catch (_) { /* already gone */ }
    }
    activeRadars.clear();
}

/**
 * Helm flipped the "Trust wire bearing" opt-in. Updates every
 * active overlay so the next sweep applies the new index path
 * without needing a disable/re-enable round-trip. Newly-enabled
 * overlays continue to pick up their initial value from the
 * enableRadarOverlay cfg.
 */
export function setRadarUseWireBearing(value) {
    const v = !!value;
    for (const rec of activeRadars.values()) rec.useWireBearing = v;
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

// ---------------------------------------------------------------------
// Helpers used by the range-ring rendering. Kept module-scoped so
// they're shared across overlays + tested implicitly by the rendering
// (no per-radar state inside).
// ---------------------------------------------------------------------

/**
 * Lat/lon `metres` due true-north of (`lat`, `lon`). Used to anchor
 * each range-ring's label at the top of the ring. Spherical-Earth
 * approximation (111_320 m per degree of latitude); accurate enough
 * for label placement at typical radar ranges (< 32 nm) - a label
 * a metre off the ring isn't visible at any plausible zoom.
 */
function offsetNorthMetres(lat, lon, metres) {
    const dLat = metres / 111_320;
    return [lat + dLat, lon];
}

/**
 * Distance label HTML for a radar range ring. The number + unit
 * formatting (decimals + trailing-zero strip) lives in C# (Format
 * .RangeRingLabel) and is mirrored in format.js. Here we wrap the
 * unit suffix in a span so CSS can dim " nm" relative to the digit
 * (the AIS guard ring uses the bare label without the span).
 */
function formatRangeLabel(metres) {
    const label = rangeRingLabel(metres / 1852);
    if (!label) return '';
    return label.replace(/ nm$/, '<span class="radar-ring-label-unit"> nm</span>');
}

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
        // Off by default: wire `bearing` is provider-defined and at
        // least one production provider (Mayara) populates it with the
        // radar's HS-corrected value instead of true-north. Default
        // path composes index from angle + boat-heading, which gives
        // the correct paint as long as boatState.headingRad is true-
        // north (see boatState comment above).
        this.useWireBearing = !!cfg.useWireBearing;

        // Canvas side = maxSpokeLen, two range cells per pixel. For
        // a typical maxSpokeLen=1024 that's a 1024x1024 buffer = 4 MB
        // of ImageData. A 1:1 cell-to-pixel layout would cost 4x the
        // memory (16 MB) and buys no visible detail: the overlay is
        // CSS-scaled down to ~600 px on screen at typical helm zoom.
        this.canvasSize = this.maxSpokeLen;
        this.canvas = document.createElement('canvas');
        this.canvas.width = this.canvasSize;
        this.canvas.height = this.canvasSize;
        // Default 2D context (GPU-backed where available). We never
        // call getImageData on this canvas - the paint loop writes
        // into an offline ImageData buffer and uploads via
        // putImageData - so willReadFrequently would force software
        // compositing for no benefit, making every map pan / zoom
        // CPU-route the full ImageData through the main thread.
        this.ctx = this.canvas.getContext('2d');
        this.ctx.imageSmoothingEnabled = false;
        // Transparent backdrop: we putImageData so every pixel starts
        // at 0 alpha until a spoke paints over it.
        this.imageData = this.ctx.createImageData(this.canvasSize, this.canvasSize);

        // Uint32 view over the same RGBA buffer. The paint loop writes
        // one 32-bit word per pixel instead of four bytes (~1.5x faster
        // on the filled-pixel branch; the open-water "already
        // transparent" early-out is unchanged). The byteOffset+length
        // form is defensive in case the runtime ever puts the data
        // somewhere other than offset 0 of its underlying buffer.
        // Endianness: every supported browser is little-endian, so the
        // R-byte sits in the low byte of each uint32 and the palette's
        // identical layout means lut32[b] is a valid pixel value as-is.
        this.imageData32 = new Uint32Array(
            this.imageData.data.buffer,
            this.imageData.data.byteOffset,
            this.imageData.data.byteLength / 4);

        // Precompute polar -> pixel LUT: for each spoke angle + range
        // cell, store the integer (x, y) to paint. Two Int16Arrays;
        // 2 bytes * spokes * maxSpokeLen each.
        //   typical 2048 x 1024: 2048 * 1024 * 2 = 4 MB per array,
        //   8 MB total.
        this.xLut = new Int16Array(this.spokes * this.maxSpokeLen);
        this.yLut = new Int16Array(this.spokes * this.maxSpokeLen);
        this._computeLuts();

        // Byte -> RGBA LUT. Backing Uint8ClampedArray for setLegend's
        // byte-level writes; Uint32 view over the same buffer for the
        // paint loop's one-word-per-pixel store. Both views share
        // memory so updates via `byteToRgba` are immediately visible
        // through `byteToRgba32` (no second rebuild needed in
        // _setLegend).
        this.byteToRgba = new Uint8ClampedArray(256 * 4);
        this.byteToRgba32 = new Uint32Array(this.byteToRgba.buffer);
        this._setLegend(cfg.legend);

        // Leaflet layer; added in _ensureLayer() once we have a
        // boat position, so the canvas doesn't briefly appear over
        // the wrong side of the map before the first GPS fix.
        this.layer = null;

        this.ws = null;
        this.lastRange = 0;
        this.destroyed = false;

        // rAF throttle for the CSS-position update path. We only
        // actually touch DOM when the boat moves or the range
        // changes; spoke paints directly mutate the canvas pixels
        // and don't need a reposition.
        this._refreshPending = false;

        // rAF coalescer for putImageData uploads. Spoke painting
        // mutates this.imageData32 synchronously per WS frame, but
        // the GPU upload is deferred to the next animation frame so
        // multiple WS frames landing inside one rAF batch into a
        // single upload of their union dirty rect. Persistent across
        // frames; reset to "empty" after each flush.
        this._paintPending = false;
        this._paintDirty = {
            minX: Infinity, minY: Infinity,
            maxX: -Infinity, maxY: -Infinity,
        };
    }

    _computeLuts() {
        // Polar origin at the geometric centre of the canvas. Using
        // (canvasSize - 1) / 2 places the centre on the boundary
        // between two pixel rows; combined with Math.round below it
        // guarantees every (x, y) lookup stays inside [0, canvasSize-1]
        // even at the cardinal extremes (r = maxSpokeLen - 1).
        const cx = (this.canvasSize - 1) / 2;
        const cy = (this.canvasSize - 1) / 2;
        // canvas diameter spans 2*maxSpokeLen radial cells, so each
        // cell maps to canvasSize / (2*maxSpokeLen) pixels. For the
        // 1:2 layout we chose (canvasSize = maxSpokeLen) that's 0.5.
        const pixelsPerCell = this.canvasSize / (2 * this.maxSpokeLen);
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
                // TRANSPARENT declared further down; referenced here
                // from a method that runs at legend-set time, well
                // after module init. ESLint's static pass can't see
                // through the closure.
                // eslint-disable-next-line no-use-before-define
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
            // Spec's pixels[] is aligned to byte value - index N
            // corresponds to byte value N.
            for (let i = 0; i < legend.pixels.length && i < 256; i++) {
                const p = legend.pixels[i];
                if (shouldSuppressLowReturn(p, i, legend)) {
                    this.byteToRgba[i * 4 + 0] = 0;
                    this.byteToRgba[i * 4 + 1] = 0;
                    this.byteToRgba[i * 4 + 2] = 0;
                    this.byteToRgba[i * 4 + 3] = 0;
                    continue;
                }
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
        // Range rings appear immediately if the boat fix is already
        // known (helm pre-zoomed in before enabling the overlay) -
        // otherwise the next boat-state push triggers a refresh via
        // onBoatStateChanged. No-op when ringsEnabled is false.
        this.refreshRangeRings();
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
        // Reset the backoff once we successfully open. A flaky link
        // that drops repeatedly should re-stretch the wait, but the
        // first reconnect after a long-stable session shouldn't have
        // to climb back up the ladder.
        ws.addEventListener('open', () => { this._reconnectMs = null; });
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
        // Exponential backoff capped at 60 s. A permanently-dead
        // radar with a flat 3 s retry burns battery on phone helms
        // (waking the radio every three seconds for a connect that
        // will never succeed); doubling each attempt up to a minute
        // matches what a thoughtful operator would tolerate while
        // still recovering quickly when the link comes back. Reset
        // happens on successful open.
        if (this._reconnectMs == null) this._reconnectMs = 3000;
        const delay = this._reconnectMs;
        this._reconnectMs = Math.min(delay * 2, 60000);
        setTimeout(() => this._openWebsocket(), delay);
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

        // Paint the new wedge. The painter folds clear-stale into
        // the same loop as paint-new (writes alpha=0 where the new
        // spoke says "no echo"), so each cell is touched at most
        // once per batch instead of twice. Dirty-rect accumulator
        // is the per-overlay persistent one so multiple WS frames
        // landing inside one rAF coalesce into a single upload.
        const dirty = this._paintDirty;
        for (const spoke of spokes) {
            this._paintSpoke(spoke, dirty);
        }

        // Schedule the GPU upload for the next animation frame.
        // Skip if no pixels actually changed (open water + new
        // spoke matched what was already there). No
        // _scheduleReposition() here: the canvas element is
        // directly parented in the overlay pane, so pixel updates
        // show up without any DOM movement. Reposition is driven
        // solely by boat-state / range changes.
        if (dirty.maxX >= dirty.minX && dirty.maxY >= dirty.minY) {
            this._schedulePaint();
        }
        // On first-ever frame, make sure the canvas is actually in
        // the overlay pane. If no boat fix yet, the layer stays
        // detached and the canvas is invisible - correct.
        if (!this.layer) this._scheduleReposition();
    }

    _clearCanvas() {
        // Reuse the existing ImageData buffer rather than reallocating
        // every range change. Uint32 fill is one word per iteration
        // vs Uint8's byte-per-iteration; both compile to a memset on
        // hot V8 but the typed-array fill path stays cleaner when the
        // JIT warms up, and it pairs with the Uint32 paint loop's
        // view of the same buffer.
        this.imageData32.fill(0);
        this.ctx.clearRect(0, 0, this.canvasSize, this.canvasSize);
        // A clear wipes the visible canvas immediately via clearRect,
        // so any dirty rect accumulated for the next rAF upload now
        // refers to pixels that have been zeroed in the buffer too.
        // Uploading them would be a no-op; reset so the flush is
        // empty if no fresh spokes have painted yet.
        const d = this._paintDirty;
        d.minX = Infinity; d.minY = Infinity;
        d.maxX = -Infinity; d.maxY = -Infinity;
    }

    _schedulePaint() {
        if (this._paintPending) return;
        this._paintPending = true;
        requestAnimationFrame(() => {
            this._paintPending = false;
            this._flushPaint();
        });
    }

    _flushPaint() {
        if (this.destroyed) return;
        const d = this._paintDirty;
        if (d.maxX < d.minX || d.maxY < d.minY) return;
        const dw = d.maxX - d.minX + 1;
        const dh = d.maxY - d.minY + 1;
        this.ctx.putImageData(this.imageData, 0, 0, d.minX, d.minY, dw, dh);
        d.minX = Infinity; d.minY = Infinity;
        d.maxX = -Infinity; d.maxY = -Infinity;
    }

    /**
     * Paints one spoke into the ImageData and grows the dirty-rect
     * accumulator. Folds the previous "clear stale, then paint" two-
     * pass into one: when the new spoke's byte is transparent we
     * zero the prior alpha (so a moving target leaves no trail), but
     * skip the write entirely if the cell was already transparent -
     * dominant case in open water, saves both the write and a dirty-
     * rect entry.
     *
     * Hot loop: one Uint32 store per filled pixel via the byteToRgba32
     * + imageData32 views. Equivalent to four Uint8ClampedArray writes
     * but ~1.5x faster on V8 / SpiderMonkey for the filled-pixel
     * branch. The "transparent stays transparent" branch is unchanged
     * because zero-equality on a single uint32 is the same work as
     * the previous alpha-byte check.
     *
     * @param {Spoke} spoke
     * @param {{minX:number,minY:number,maxX:number,maxY:number}} dirty
     */
    _paintSpoke(spoke, dirty) {
        const spokeIdx = this._spokeIndex(spoke);
        const base = spokeIdx * this.maxSpokeLen;
        const data32 = this.imageData32;
        const w = this.canvasSize;
        const lut32 = this.byteToRgba32;
        const xLut = this.xLut;
        const yLut = this.yLut;
        const len = Math.min(spoke.data.length, this.maxSpokeLen);
        const bytes = spoke.data;
        for (let r = 0; r < len; r++) {
            const b = bytes[r];
            // Single packed lookup: 0xAABBGGRR on little-endian. A
            // fully-transparent palette entry (legend's "no echo" or
            // suppressed low-return) has all four bytes zero, so
            // lut32[b] === 0 is the equivalent of the old
            // newAlpha === 0 check.
            const rgba = lut32[b];
            const x = xLut[base + r];
            const y = yLut[base + r];
            const idx = y * w + x;
            if (rgba === 0) {
                // No echo. Skip the write (and the dirty-rect grow)
                // when the cell was already transparent - dominant
                // case in open water.
                if (data32[idx] === 0) continue;
                data32[idx] = 0;
            } else {
                data32[idx] = rgba;
            }
            if (x < dirty.minX) dirty.minX = x;
            if (x > dirty.maxX) dirty.maxX = x;
            if (y < dirty.minY) dirty.minY = y;
            if (y > dirty.maxY) dirty.maxY = y;
        }
    }

    /** Map a spoke's wire angle/bearing onto our canvas's north-up
     *  spoke index. Pure decision delegated to computeSpokeIndex
     *  so the policy can be unit-tested without a canvas/Leaflet
     *  stub; see computeSpokeIndex below for the rules. */
    _spokeIndex(spoke) {
        return computeSpokeIndex(spoke, boatState.headingRad, this.spokes, this.useWireBearing);
    }

    setRange(range) {
        if (!range || range === this.range) return;
        // Clear before the bounds recalc that follows. The painted
        // pixels are at the OLD scale: spoke cell N maps to a fixed
        // canvas pixel via the range-independent LUT, but the canvas
        // is about to be repositioned to cover a new geographic
        // square. Without the clear, an echo painted at "1 nm" pixel
        // position is shown at the new range's "1 nm" geographic
        // position, which is a different physical place. The wire-
        // driven branch in _onFrame already does this clear when the
        // radar's per-spoke range field changes; mirror that here so
        // the helm-driven path doesn't depend on the radar reporting
        // a range value (Mayara's protobuf zero-defaults the field
        // when the radar omits it). Empty-and-honest beats stale-
        // and-misplaced for a navigation overlay.
        this._clearCanvas();
        this.range = range;
        this._scheduleReposition();
        // Range scrolls the rings: the radii are fractions of
        // this.range, so a new range needs new circles.
        this.refreshRangeRings();
    }

    onBoatStateChanged() {
        this._scheduleReposition();
        // Boat moved -> rings follow. Cheap setLatLng on existing
        // circles when the group already exists; a full refresh on
        // the first fix (group is null until then).
        if (this._rangeRings) this._updateRangeRingsCentre();
        else this.refreshRangeRings();
    }

    /**
     * Build (or rebuild) the concentric range-ring polylines + their
     * distance labels for this radar. Tears down any existing group
     * first so the call is idempotent. Rings are L.circle (radius in
     * metres, properly projected by Leaflet) so the geometry stays
     * correct across zoom levels without us re-doing the haversine
     * math.
     *
     * Visual hierarchy: the outer ring matches the configured radar
     * range (i.e. the boundary of detection) and is drawn solid +
     * heavier so the helm reads it as "this is what the radar
     * actually sees right now". Inner rings stay dashed + faint as
     * scale marks. Each ring carries a small label at the top
     * (true-north bearing from boat) showing its distance in nm.
     *
     * No-op when range / boat-fix is missing OR the helm has the
     * feature disabled.
     */
    refreshRangeRings() {
        if (this._rangeRings) {
            this._rangeRings.remove();
            this._rangeRings = null;
            this._rangeRingCircles = null;
            this._rangeRingLabels = null;
        }
        if (!ringsEnabled) return;
        const { lat, lon } = boatState;
        if (lat == null || lon == null || !this.range) return;

        const group = L.layerGroup();
        const circles = [];
        const labels = [];
        for (let i = 1; i <= ringsCount; i++) {
            const ringRange = (i / ringsCount) * this.range;
            const isActive = i === ringsCount;
            // Outer (active) ring: solid + heavier + higher opacity so
            // the helm reads it as the actual boundary of detection.
            // Inner rings: dashed + faint, "scale marks".
            const c = L.circle([lat, lon], {
                radius: ringRange,
                color: '#94a3b8',
                weight: isActive ? 2 : 1,
                opacity: isActive ? 0.9 : 0.4,
                dashArray: isActive ? null : '4,5',
                fill: false,
                interactive: false,
                pane: 'overlayPane',
            }).addTo(group);
            circles.push(c);

            // Distance label, anchored at the top of each ring (due
            // true-north from boat). One label per ring; the active
            // one's font is heavier to match the ring stroke.
            // Class is `radar-ring-label` (NOT `radar-range-label` -
            // the latter exists for the HUD-side range chip).
            const labelLatLng = offsetNorthMetres(lat, lon, ringRange);
            const className = 'radar-ring-label'
                + (isActive ? ' radar-ring-label--active' : '');
            const labelMarker = L.marker(labelLatLng, {
                icon: L.divIcon({
                    className,
                    html: formatRangeLabel(ringRange),
                    // Anchor the bottom-centre of the label box at the
                    // ring vertex so the text sits ABOVE the ring line,
                    // not crossing it. iconSize must be set for divIcon
                    // even when CSS handles the visual width.
                    iconSize: [60, 18],
                    iconAnchor: [30, 18],
                }),
                interactive: false,
                keyboard: false,
                pane: 'overlayPane',
            }).addTo(group);
            labels.push({ marker: labelMarker, range: ringRange });
        }
        group.addTo(this.map);
        this._rangeRings = group;
        this._rangeRingCircles = circles;
        this._rangeRingLabels = labels;
    }

    _updateRangeRingsCentre() {
        if (!this._rangeRingCircles) return;
        const { lat, lon } = boatState;
        if (lat == null || lon == null) return;
        for (const c of this._rangeRingCircles) c.setLatLng([lat, lon]);
        // Label markers track the ring radius, so recompute their
        // lat/lon as the boat moves. Same labels reused - the text
        // doesn't change, only the position.
        if (this._rangeRingLabels) {
            for (const l of this._rangeRingLabels) {
                l.marker.setLatLng(offsetNorthMetres(lat, lon, l.range));
            }
        }
    }

    _scheduleReposition() {
        if (this._refreshPending) return;
        this._refreshPending = true;
        requestAnimationFrame(() => {
            this._refreshPending = false;
            this._reposition();
        });
    }

    _reposition() {
        if (this.destroyed) return;
        const { lat, lon } = boatState;
        if (lat == null || lon == null) return;

        this._ensureLayer(lat, lon);
        this.layer.updateAnchor(lat, lon, this.range);
    }

    _ensureLayer(lat, lon) {
        if (this.layer) return;
        this.canvas.style.opacity = String(this.opacity);
        this.canvas.classList.add('radar-overlay');
        // CanvasGeoLayer declared further down (line ~595). Method
        // runs at first-spoke time, safely after module init.
        // eslint-disable-next-line no-use-before-define
        this.layer = new CanvasGeoLayer(this.canvas, lat, lon, this.range);
        this.layer.addTo(this.map);
    }

    destroy() {
        this.destroyed = true;
        if (this.ws) {
            try { this.ws.close(); } catch { /* ignore */ }
            this.ws = null;
        }
        if (this.layer) {
            this.layer.remove();
            this.layer = null;
        }
        if (this._rangeRings) {
            this._rangeRings.remove();
            this._rangeRings = null;
            this._rangeRingCircles = null;
            this._rangeRingLabels = null;
        }
        // Release the LUT memory aggressively - these can be
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

/** Defensive positive-modulo into [0, n). JS `%` preserves the sign
 *  of the dividend; a signed-number corruption from a non-conforming
 *  provider would otherwise produce a negative index and an out-of-
 *  bounds LUT read (silently wrong pixels). Cheap to guard, expensive
 *  to diagnose. */
function wrapSpoke(i, n) {
    return ((i % n) + n) % n;
}

/** Convert the boat's heading (radians, 0..2pi from true north) into
 *  the spoke-index offset that aligns a bow-relative `angle` field
 *  onto the canvas's north-up grid. Spokes per revolution determines
 *  the discretisation. */
function headingToSpokeOffset(headingRad, spokesPerRevolution) {
    return Math.round(headingRad * spokesPerRevolution / (2 * Math.PI));
}

/**
 * Decide which spoke index on the north-up canvas to paint into.
 *
 * Default (useWireBearing=false): compose `angle + heading` from the
 * spoke's bow-relative angle plus the boat's TRUE-NORTH heading. The
 * wire's optional `bearing` field is ignored because at least one
 * production provider (Mayara) fills it with the radar's internal
 * HS-corrected value rather than true-north - paints every spoke
 * bow-up when the radar has no heading sensor wired in.
 *
 * Opt-in (useWireBearing=true): trust the wire's `bearing` as
 * true-north when present. For helms whose provider verifiably
 * emits true-north bearings; saves one add per spoke at the cost
 * of correctness on non-conforming providers.
 *
 * Both paths funnel through wrapSpoke for the defensive modulo.
 *
 * @param {{angle: number, bearing?: number}} spoke
 * @param {number} headingRad   Boat heading (radians, 0..2pi from true north).
 * @param {number} spokesPerRevolution
 * @param {boolean} useWireBearing
 */
function computeSpokeIndex(spoke, headingRad, spokesPerRevolution, useWireBearing) {
    if (useWireBearing && spoke.bearing != null) {
        return wrapSpoke(spoke.bearing, spokesPerRevolution);
    }
    return wrapSpoke(
        spoke.angle + headingToSpokeOffset(headingRad, spokesPerRevolution),
        spokesPerRevolution);
}

/** Decide whether a legend entry should be rendered transparent.
 *  Drives the "drop sea-clutter" UX: typical recreational radar
 *  palettes paint low-intensity normal echoes (sea clutter, noise)
 *  as a blue ramp AND the medium-strength normals also lean blue /
 *  cyan / blue-green. The user's complaint is visual ("anything that
 *  looks blue draws too much attention regardless of intensity"), so
 *  we combine two checks for normal pixels:
 *    1. Metadata: byte indices 1..mediumReturn-1 are sea clutter
 *       per the legend's own classification (covers ramps where the
 *       provider doesn't pick blue but still flags noise).
 *    2. Colour: anything where blue is the dominant channel
 *       (B > R AND B > G) - catches the cyan / pure-blue / blue-green
 *       ramp that on common palettes extends past the metadata cutoff
 *       (bytes 5-7 are still blue-dominant by RGB even though they're
 *       above mediumReturn).
 *  Doppler / history / target-border markers stay visible regardless
 *  of colour because the check is gated on type === 'normal'.
 *  Exported via `_internal` for test coverage. */
function shouldSuppressLowReturn(pixel, index, legend) {
    if (!pixel || pixel.type !== 'normal') return false;
    if (typeof legend?.mediumReturn === 'number'
        && index >= 1 && index < legend.mediumReturn) return true;
    const rgba = parseLegendColor(pixel.color);
    return rgba[2] > rgba[0] && rgba[2] > rgba[1];
}

// Exposed for tests; not part of the public interop API.
export const _internal = {
    DEFAULT_LEGEND_PIXELS,
    parseHexRgba,
    parseLegendColor,
    shouldSuppressLowReturn,
    wrapSpoke,
    headingToSpokeOffset,
    computeSpokeIndex,
};

// ---------------------------------------------------------------------
// CanvasGeoLayer: minimal Leaflet L.Layer subclass that parents a
// provided <canvas> directly into the overlay pane. The canvas is
// positioned + CSS-scaled to occupy a geographic square of
// `rangeMeters * 2` centred on an anchor lat/lon. Pixel writes into
// the canvas show up immediately; we only touch the DOM when the
// anchor / range / map viewport changes.
//
// vs. L.ImageOverlay: ImageOverlay needs a URL and encodes the canvas
// on every `setUrl`. For a 4096x4096 canvas toDataURL costs 10-50 ms
// on the main thread - unacceptable at radar frame rates. This
// layer avoids that entirely.
// ---------------------------------------------------------------------

const CanvasGeoLayer = L.Layer.extend({
    initialize(canvasEl, lat, lon, rangeMeters) {
        this._canvas = canvasEl;
        this._anchorLat = lat;
        this._anchorLon = lon;
        this._range = rangeMeters;
        // Set once; per-reset we update transform + size.
        canvasEl.style.position = 'absolute';
        canvasEl.style.pointerEvents = 'none';
        // Disable the browser's anti-alias smoothing when the canvas
        // is CSS-scaled to a different display size than its native
        // pixel size - we'd rather have crisp spoke pixels than a
        // blurry upsample.
        canvasEl.style.imageRendering = 'pixelated';
    },

    onAdd(map) {
        this._map = map;
        map.getPanes().overlayPane.appendChild(this._canvas);
        // viewreset fires on zoom end; zoomanim is the in-progress
        // zoom signal that lets us keep the overlay in sync with
        // the tile layer's scale animation. Without zoomanim the
        // radar overlay would visibly "pop" to the new size only
        // when the zoom animation ends.
        map.on('zoomend viewreset', this._reset, this);
        map.on('zoomanim', this._animateZoom, this);
        this._reset();
        return this;
    },

    onRemove(map) {
        if (this._canvas.parentNode === map.getPanes().overlayPane) {
            map.getPanes().overlayPane.removeChild(this._canvas);
        }
        map.off('zoomend viewreset', this._reset, this);
        map.off('zoomanim', this._animateZoom, this);
        this._map = null;
        return this;
    },

    /** Update the geographic anchor point and/or radar range. Cheap
     *  - just a DOM transform on the canvas element; no repaint
     *  cost because the canvas pixels are already written. */
    updateAnchor(lat, lon, rangeMeters) {
        this._anchorLat = lat;
        this._anchorLon = lon;
        this._range = rangeMeters;
        this._reset();
    },

    _reset() {
        if (!this._map) return;
        const bounds = this._geoBounds();
        const topLeft = this._map.latLngToLayerPoint(bounds.getNorthWest());
        const bottomRight = this._map.latLngToLayerPoint(bounds.getSouthEast());
        const size = bottomRight.subtract(topLeft);
        L.DomUtil.setPosition(this._canvas, topLeft);
        this._canvas.style.width  = `${size.x}px`;
        this._canvas.style.height = `${size.y}px`;
    },

    // Leaflet's zoom-in-progress signal. Set a transform that
    // matches what the tilePane is doing so the overlay scales
    // + pans in step rather than jumping at zoomend. The math
    // mirrors L.ImageOverlay._animateZoom.
    _animateZoom(ev) {
        const bounds = this._geoBounds();
        const scale = this._map.getZoomScale(ev.zoom);
        const offset = this._map._latLngBoundsToNewLayerBounds(bounds, ev.zoom, ev.center).min;
        L.DomUtil.setTransform(this._canvas, offset, scale);
    },

    _geoBounds() {
        // Flat-earth square around the anchor, side = 2 * range.
        // Cosine-corrected longitude step; lat >= 85 clamps to 1
        // so we don't divide by ~0 at the poles.
        const metresPerDegLat = 111_320;
        const metresPerDegLon = 111_320 * Math.cos(this._anchorLat * Math.PI / 180) || 1;
        const dLat = this._range / metresPerDegLat;
        const dLon = this._range / metresPerDegLon;
        return L.latLngBounds(
            [this._anchorLat - dLat, this._anchorLon - dLon],
            [this._anchorLat + dLat, this._anchorLon + dLon],
        );
    },
});
