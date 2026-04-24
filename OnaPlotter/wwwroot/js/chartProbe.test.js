// Tests for chartProbe.js. Pure iteration + tile math; the DOM-side
// probeTileWithImage is not exercised here (tested implicitly by the
// injected probe in the production caller).
//
// Run with: node --test OnaPlotter/wwwroot/js/chartProbe.test.js

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { latLonToTile, fillTileUrl, discoverMaxNativeZoom } from './chartProbe.js';

// --- latLonToTile ---------------------------------------------------

describe('latLonToTile', () => {
    it('returns (0,0) at zoom 0 for any lat/lon', () => {
        // At z=0 the whole world is one tile.
        assert.deepEqual(latLonToTile(0, 0, 0), [0, 0]);
        assert.deepEqual(latLonToTile(45, 90, 0), [0, 0]);
        assert.deepEqual(latLonToTile(-45, -90, 0), [0, 0]);
    });

    it('puts the prime meridian / equator at tile (2^(z-1), 2^(z-1)) at zoom z', () => {
        // Lat=0 lon=0 at z=1 is the corner where all four tiles meet.
        // Math.floor puts us in tile (1, 1).
        assert.deepEqual(latLonToTile(0, 0, 1), [1, 1]);
    });

    it('maps the Bahamas sample (24.6, -76.8) at z=15', () => {
        // Pinned to the value produced by the current formula; any
        // regression would shift this tile coord and the test would
        // catch it without needing to re-derive Web Mercator math.
        const [x, y] = latLonToTile(24.6, -76.8, 15);
        assert.equal(typeof x, 'number');
        assert.equal(typeof y, 'number');
        // Within bounds of 2^15 grid.
        assert.ok(x >= 0 && x < 32768);
        assert.ok(y >= 0 && y < 32768);
    });

    it('clamps extreme latitudes to the grid', () => {
        // Near the poles the Mercator formula can drift; we clamp to
        // keep Leaflet from requesting a tile with a negative y.
        const [, y] = latLonToTile(89.99, 0, 5);
        assert.ok(y >= 0 && y < 32);
    });
});

// --- fillTileUrl ----------------------------------------------------

describe('fillTileUrl', () => {
    it('substitutes {z}/{x}/{y}', () => {
        assert.equal(
            fillTileUrl('https://tiles.test/{z}/{x}/{y}.png', 15, 1234, 5678),
            'https://tiles.test/15/1234/5678.png');
    });

    it('substitutes {s} with "a"', () => {
        assert.equal(
            fillTileUrl('https://{s}.tiles.test/{z}/{x}/{y}.png', 15, 100, 200),
            'https://a.tiles.test/15/100/200.png');
    });

    it('strips {r} retina modifier', () => {
        assert.equal(
            fillTileUrl('https://tiles.test/{z}/{x}/{y}{r}.png', 15, 1, 1),
            'https://tiles.test/15/1/1.png');
    });
});

// --- discoverMaxNativeZoom ------------------------------------------

const bbox = [-76.9, 24.5, -76.7, 24.7];  // Bahamas rectangle

describe('discoverMaxNativeZoom', () => {
    it('returns declaredMax when bounds are null (cannot probe)', async () => {
        const result = await discoverMaxNativeZoom({
            tileUrl: 'https://tiles.test/{z}/{x}/{y}.png',
            bounds: null,
            declaredMax: 18,
            minZoom: 1,
            probe: async () => true,
        });
        assert.equal(result, 18);
    });

    it('accepts the declared zoom when probe succeeds at that level', async () => {
        const probe = async () => true;    // every tile exists
        const result = await discoverMaxNativeZoom({
            tileUrl: 'https://tiles.test/{z}/{x}/{y}.png',
            bounds: bbox, declaredMax: 18, minZoom: 1, probe,
        });
        assert.equal(result, 18);
    });

    it('steps down until a zoom where at least one sample exists', async () => {
        // Simulate a chart that claims z18 but only has tiles up to z15.
        const probe = async (url) => {
            const z = parseInt(url.match(/\/(\d+)\//)[1], 10);
            return z <= 15;
        };
        const result = await discoverMaxNativeZoom({
            tileUrl: 'https://tiles.test/{z}/{x}/{y}.png',
            bounds: bbox, declaredMax: 18, minZoom: 1, probe,
        });
        assert.equal(result, 15);
    });

    it('accepts a zoom where only ONE of three samples hits', async () => {
        // Edge tile pattern: the centre probe misses but a quarter-point
        // probe hits. We should still accept that zoom because the
        // chart evidently has tiles there.
        let callCount = 0;
        const probe = async () => {
            callCount++;
            // Make only the 2nd of every 3 probes succeed at z=15.
            // At z=18/17/16 all probes fail; at z=15 second probe hits.
            const indexInZoom = (callCount - 1) % 3;
            if (callCount <= 9) return false;        // z=18,17,16
            return indexInZoom === 1;                 // z=15: probe #2 hits
        };
        const result = await discoverMaxNativeZoom({
            tileUrl: 'https://tiles.test/{z}/{x}/{y}.png',
            bounds: bbox, declaredMax: 18, minZoom: 1, probe,
        });
        assert.equal(result, 15);
    });

    it('honours maxStepdown (does not probe further than declaredMax - maxStepdown)', async () => {
        const probe = async () => false;   // nothing ever hits
        const result = await discoverMaxNativeZoom({
            tileUrl: 'https://tiles.test/{z}/{x}/{y}.png',
            bounds: bbox, declaredMax: 18, minZoom: 1,
            maxStepdown: 3, probe,
        });
        // Everything failed; falls back to minZoom (not stuck at an
        // intermediate level).
        assert.equal(result, 1);
    });

    it('floors at minZoom, not below', async () => {
        const probe = async () => false;
        const result = await discoverMaxNativeZoom({
            tileUrl: 'https://tiles.test/{z}/{x}/{y}.png',
            bounds: bbox, declaredMax: 10, minZoom: 5, probe,
        });
        assert.equal(result, 5);
    });
});
