// Tests for chartOverzoom.js. Pure functions: no Leaflet, no DOM.
// Run with: node --test OnaPlotter/wwwroot/js/chartOverzoom.test.js

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { isOverlayChart, computeOverzoom, applyOverzoom } from './chartOverzoom.js';
import { MarkerLayer } from './markerLayer.js';

// --- isOverlayChart -------------------------------------------------

describe('isOverlayChart', () => {
    it('recognises the canonical OpenSeaMap identifier', () => {
        assert.equal(isOverlayChart('openseamap', ''), true);
    });

    it('is case-insensitive', () => {
        assert.equal(isOverlayChart('OpenSeaMap', ''), true);
        assert.equal(isOverlayChart('OPENSEAMAP', ''), true);
    });

    it('matches any id containing "seamark"', () => {
        // A user might install a seamark overlay under a different id;
        // the substring match still classifies it correctly.
        assert.equal(isOverlayChart('noaa-seamarks', ''), true);
        assert.equal(isOverlayChart('seamark-demo', ''), true);
    });

    it('matches any tile URL with "/seamark/" segment', () => {
        // External OpenSeaMap CDN URL.
        assert.equal(
            isOverlayChart('custom-id', 'https://tiles.openseamap.org/seamark/{z}/{x}/{y}.png'),
            true);
        // SignalK-proxied URL.
        assert.equal(
            isOverlayChart('whatever', '/signalk/chart-tiles/seamark/{z}/{x}/{y}'),
            true);
    });

    it('returns false for regular base chart identifiers', () => {
        // These are all plausible real-world base chart ids. None should
        // be flagged as an overlay -- they'd otherwise always overzoom,
        // and detailed harbour charts wouldn't kick in correctly.
        assert.equal(isOverlayChart('noaa', 'https://x/{z}/{x}/{y}'), false);
        assert.equal(isOverlayChart('osm', 'https://tile.openstreetmap.org/{z}/{x}/{y}.png'), false);
        assert.equal(isOverlayChart('my-rnc', '/signalk/chart-tiles/my-rnc/{z}/{x}/{y}'), false);
    });

    it('handles null / undefined / empty inputs without throwing', () => {
        // Regression guard: chart records occasionally arrive with
        // missing fields while the UI waits for a REST fetch.
        assert.equal(isOverlayChart(null, null), false);
        assert.equal(isOverlayChart(undefined, undefined), false);
        assert.equal(isOverlayChart('', ''), false);
    });

    it('does not match substring "seamarker" -- wait yes it does', () => {
        // Document the false-positive envelope: the substring match is
        // intentional to catch user-customised ids, but it will snag
        // anything containing the letters "seamark". That's acceptable
        // since the rendering cost is nil (overlays just always overzoom).
        assert.equal(isOverlayChart('seamarker-base', ''), true);
    });
});

// --- computeOverzoom ------------------------------------------------

// Small helpers to keep each test's setup readable.
const m = (obj) => new Map(Object.entries(obj));
const s = (...ids) => new Set(ids);

describe('computeOverzoom', () => {
    it('empty input produces empty output', () => {
        const out = computeOverzoom(new Map(), new Set());
        assert.equal(out.size, 0);
    });

    it('single base chart gets overzoomed to 22+', () => {
        // With only one chart, nothing to compete with -- it's the top
        // native by default and should stretch past its native.
        const out = computeOverzoom(m({ noaa: 18 }), s());
        assert.equal(out.get('noaa'), 22);
    });

    it('a native-24 chart overzooms to native + 4, not capped at 22', () => {
        const out = computeOverzoom(m({ detailed: 24 }), s());
        assert.equal(out.get('detailed'), 28);
    });

    it('two base charts of different natives: only the top overzooms', () => {
        // Wide-area native 12 must NOT stretch over the detailed native
        // 18 chart. The detailed one gets overzoom; the wide-area keeps
        // its native so it naturally hides past its limit.
        const out = computeOverzoom(m({ wide: 12, detailed: 18 }), s());
        assert.equal(out.get('wide'), 12);
        assert.equal(out.get('detailed'), 22);
    });

    it('two base charts tied at top: both overzoom', () => {
        // Tie is acceptable -- both stretch; the later-added one draws
        // on top anyway.
        const out = computeOverzoom(m({ a: 18, b: 18 }), s());
        assert.equal(out.get('a'), 22);
        assert.equal(out.get('b'), 22);
    });

    it('OPENSEAMAP REGRESSION: base-18 next to overlay-18 still overzooms the base', () => {
        // The bug this module exists to prevent: if the overlay were
        // counted in the "top native" race, the overlay's 18 would tie
        // and trigger the base chart's overzoom... OR, more insidiously,
        // if the overlay were 19 the base (17) would be pinned to 17.
        //
        // Case A: equal natives. Base should still overzoom.
        let out = computeOverzoom(m({ noaa: 18, openseamap: 18 }), s('openseamap'));
        assert.equal(out.get('noaa'), 22, 'base tied with overlay must still overzoom');
        assert.equal(out.get('openseamap'), 22, 'overlay always overzooms');

        // Case B: overlay has a higher native than the base. Base must
        // still overzoom because the overlay is excluded from the race.
        out = computeOverzoom(m({ noaa: 17, openseamap: 19 }), s('openseamap'));
        assert.equal(out.get('noaa'), 22, 'base must overzoom even when overlay native is higher');
        assert.equal(out.get('openseamap'), 23);

        // Case C: overlay has a LOWER native than the base. Both overzoom,
        // overlay is always overzoomed regardless.
        out = computeOverzoom(m({ noaa: 18, openseamap: 15 }), s('openseamap'));
        assert.equal(out.get('noaa'), 22);
        assert.equal(out.get('openseamap'), 22, 'overlay always overzooms to min 22');
    });

    it('overlays never count toward the "top native" race', () => {
        // With two base charts (natives 12 and 18) plus an overlay
        // whose native is 25, the overlay must NOT become the top
        // native and force the detailed (18) base to stop overzooming.
        const out = computeOverzoom(
            m({ wide: 12, detailed: 18, openseamap: 25 }),
            s('openseamap'));
        assert.equal(out.get('wide'), 12, 'non-top base stays pinned to native');
        assert.equal(out.get('detailed'), 22, 'top base still overzooms');
        assert.equal(out.get('openseamap'), 29, 'overlay overzooms by native + 4');
    });

    it('all-overlay set produces a valid map (no base to race)', () => {
        // Edge: user unloads every base chart, leaving only an overlay.
        // topNative stays 0; overlay still overzooms because it matches
        // the overlay-overzoom branch first.
        const out = computeOverzoom(m({ openseamap: 18 }), s('openseamap'));
        assert.equal(out.get('openseamap'), 22);
    });

    it('is deterministic across calls', () => {
        // Two identical inputs must produce identical outputs; no
        // hidden state in the module.
        const a = computeOverzoom(m({ a: 12, b: 18 }), s());
        const b = computeOverzoom(m({ a: 12, b: 18 }), s());
        assert.deepEqual([...a.entries()], [...b.entries()]);
    });
});

// --- applyOverzoom ---------------------------------------------------
//
// The side-effecting sibling of computeOverzoom. Tests run against a
// real MarkerLayer instance (not a MockMap) because the ORIGINAL bug
// that motivated this suite -- `chartLayers.map.size is undefined` --
// only manifests when callers reach for a shape MarkerLayer doesn't
// have. Plugging in a real MarkerLayer here pins the integration point.

describe('applyOverzoom', () => {
    // Fake Leaflet layer: just a plain object with options.maxZoom and
    // a redraw() spy. That's the only contract applyOverzoom needs.
    function fakeLayer(nativeMax) {
        const l = { options: { maxZoom: nativeMax }, redraws: 0 };
        l.redraw = () => l.redraws++;
        return l;
    }

    it('empty id list is a no-op (early return, no getLayer calls)', () => {
        let getLayerCalls = 0;
        let setEffMaxCalls = 0;
        applyOverzoom([], new Map(), new Set(),
            () => { getLayerCalls++; return null; },
            () => { setEffMaxCalls++; });
        assert.equal(getLayerCalls, 0);
        assert.equal(setEffMaxCalls, 0);
    });

    it('null id list is a no-op (defensive)', () => {
        // recomputeChartOverzoom used to early-return on size === 0;
        // applyOverzoom should be equally lenient so the caller can
        // just pass the keys array without guarding.
        let called = false;
        applyOverzoom(null, new Map(), new Set(), () => null, () => { called = true; });
        assert.equal(called, false);
    });

    it('updates each layer via setEffMax with the computed maxZoom', () => {
        // Use a REAL MarkerLayer as the layer backing store so the call
        // `(id) => chartLayers.get(id)` passed from recomputeChartOverzoom
        // exercises the actual API shape. This is the test that would
        // have caught the original `chartLayers.map.size` bug.
        const chartLayers = new MarkerLayer(null);
        const a = fakeLayer(12);
        const b = fakeLayer(18);
        chartLayers.set('wide', a);
        chartLayers.set('detailed', b);
        const nativeMax = new Map([['wide', 12], ['detailed', 18]]);

        const applied = new Map();
        applyOverzoom(chartLayers.keys(), nativeMax, new Set(),
            (id) => chartLayers.get(id),
            (id, layer, effMax) => applied.set(id, effMax));

        // 'wide' not top-native -> pinned. 'detailed' top -> overzoomed.
        assert.equal(applied.get('wide'), 12);
        assert.equal(applied.get('detailed'), 22);
    });

    it('skips ids whose layer lookup returns falsy', () => {
        // Between recomputeChartOverzoom reading keys() and calling
        // getLayer, a concurrent remove might nil out a layer. Defensive
        // skip rather than letting `layer.options` explode.
        const nativeMax = new Map([['a', 18], ['b', 18]]);
        const setCalls = [];
        applyOverzoom(['a', 'b'], nativeMax, new Set(),
            (id) => id === 'a' ? { options: {} } : null,
            (id, _layer, effMax) => setCalls.push([id, effMax]));
        assert.deepEqual(setCalls.map(c => c[0]), ['a']);
    });

    it('regression: real chartLayers + overlay exclusion yields base overzoom', () => {
        // The end-to-end shape of the OPENSEAMAP regression, wired up
        // through a real MarkerLayer -- i.e. exactly the code path
        // that crashed in production before the fix.
        const chartLayers = new MarkerLayer(null);
        chartLayers.set('noaa', fakeLayer(17));
        chartLayers.set('openseamap', fakeLayer(19));
        const nativeMax = new Map([['noaa', 17], ['openseamap', 19]]);
        const overlays = new Set(['openseamap']);

        const applied = new Map();
        applyOverzoom(chartLayers.keys(), nativeMax, overlays,
            (id) => chartLayers.get(id),
            (id, _layer, effMax) => applied.set(id, effMax));

        assert.equal(applied.get('noaa'), 22,
            'base chart must overzoom even when the overlay native is higher');
        assert.equal(applied.get('openseamap'), 23);
    });
});
