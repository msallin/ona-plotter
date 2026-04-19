// Tests for markerLayer.js. Pure JS + a stub for the Leaflet map.
// Run with: node --test OnaPlotter/wwwroot/js/markerLayer.test.js
//
// Existence rationale: the "chart reorder does nothing / chart toggle
// crashes" production bug came from recomputeChartOverzoom typing
// `chartLayers.map.size` against a MarkerLayer that only exposes
// `.items`, `.has`, `.get`, `.keys`. A contract test on MarkerLayer
// itself pins the surface so a future caller mistyping against a
// wrong shape (e.g. expecting a native Map) fails at test time rather
// than at first-chart-toggle on a real boat.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { MarkerLayer } from './markerLayer.js';

// Minimal Leaflet-map stub: records removeLayer calls.
function makeMapStub() {
    const removed = [];
    return {
        removed,
        removeLayer(layer) { removed.push(layer); }
    };
}

describe('MarkerLayer API surface', () => {
    it('exposes the API leafletInterop callers rely on', () => {
        // Any caller mis-typing .map / .size-as-property / .map.entries()
        // should fail fast. Pin the exact names to prevent drift.
        const ml = new MarkerLayer(null);
        assert.equal(typeof ml.has, 'function');
        assert.equal(typeof ml.get, 'function');
        assert.equal(typeof ml.set, 'function');
        assert.equal(typeof ml.keys, 'function');
        assert.equal(typeof ml.remove, 'function');
        assert.equal(typeof ml.clear, 'function');
        assert.equal(typeof ml.entries, 'function');
        assert.equal(typeof ml.setMap, 'function');

        // `.size` is a getter (not a method) -- equivalent to Map.prototype.size
        // and what the originally-buggy `chartLayers.map.size` code wanted.
        assert.equal(typeof ml.size, 'number');

        // `.items` is the backing store. Exposed so current code that
        // reaches into it (outside this refactor) keeps working, but
        // new callers should prefer the methods.
        assert.deepEqual(ml.items, {});
    });

    it('empty layer reports size 0 and empty keys', () => {
        const ml = new MarkerLayer(null);
        assert.equal(ml.size, 0);
        assert.deepEqual(ml.keys(), []);
        assert.equal(ml.has('anything'), false);
        assert.equal(ml.get('anything'), undefined);
    });
});

describe('MarkerLayer mutations', () => {
    it('set / get / has round-trip', () => {
        const ml = new MarkerLayer(null);
        const layer = { fake: 1 };
        ml.set('a', layer);
        assert.equal(ml.has('a'), true);
        assert.equal(ml.get('a'), layer);
        assert.equal(ml.size, 1);
        assert.deepEqual(ml.keys(), ['a']);
    });

    it('set twice on the same id overwrites', () => {
        const ml = new MarkerLayer(null);
        const first = { v: 1 };
        const second = { v: 2 };
        ml.set('a', first);
        ml.set('a', second);
        assert.equal(ml.get('a'), second);
        assert.equal(ml.size, 1);
    });

    it('remove pulls layer off the map and deletes the entry', () => {
        const mapStub = makeMapStub();
        const ml = new MarkerLayer(mapStub);
        const layer = { id: 'L1' };
        ml.set('a', layer);

        ml.remove('a');

        assert.equal(ml.has('a'), false);
        assert.equal(ml.size, 0);
        assert.deepEqual(mapStub.removed, [layer]);
    });

    it('remove of a missing id is a no-op', () => {
        const mapStub = makeMapStub();
        const ml = new MarkerLayer(mapStub);
        ml.remove('nope');
        assert.deepEqual(mapStub.removed, []);
    });

    it('clear removes every layer from the map', () => {
        const mapStub = makeMapStub();
        const ml = new MarkerLayer(mapStub);
        ml.set('a', { id: 1 });
        ml.set('b', { id: 2 });
        ml.set('c', { id: 3 });

        ml.clear();

        assert.equal(ml.size, 0);
        assert.equal(mapStub.removed.length, 3);
    });

    it('setMap lets callers attach the map after construction', () => {
        // leafletInterop.js creates MarkerLayers at module scope, before
        // L.map(...) runs in initMap. setMap wires them up later.
        const ml = new MarkerLayer(null);
        const layer = { id: 'X' };
        ml.set('a', layer);

        const mapStub = makeMapStub();
        ml.setMap(mapStub);
        ml.remove('a');

        assert.deepEqual(mapStub.removed, [layer]);
    });

    it('remove without a map does not throw', () => {
        // Regression guard: if setMap was never called, remove() must
        // still clean the dict (just can't pull off the map, which
        // doesn't exist).
        const ml = new MarkerLayer(null);
        ml.set('a', { id: 1 });
        ml.remove('a');
        assert.equal(ml.size, 0);
    });
});

describe('MarkerLayer iteration', () => {
    it('entries() yields [id, layer] pairs', () => {
        const ml = new MarkerLayer(null);
        ml.set('a', { v: 1 });
        ml.set('b', { v: 2 });

        const pairs = [...ml.entries()];
        assert.equal(pairs.length, 2);
        assert.deepEqual(pairs[0], ['a', { v: 1 }]);
        assert.deepEqual(pairs[1], ['b', { v: 2 }]);
    });

    it('keys() snapshot is safe to iterate while mutating', () => {
        // Relied on by MarkerLayer.clear(): keys() returns a snapshot
        // from Object.keys, so remove() during iteration doesn't skip.
        const ml = new MarkerLayer(null);
        ml.set('a', { v: 1 });
        ml.set('b', { v: 2 });
        ml.set('c', { v: 3 });

        const seen = [];
        for (const id of ml.keys()) {
            seen.push(id);
            ml.remove(id);
        }
        assert.deepEqual(seen, ['a', 'b', 'c']);
        assert.equal(ml.size, 0);
    });
});
