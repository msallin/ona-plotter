// Tests for boatIcons.js SVG templates.
// Run with: node --test OnaPlotter/wwwroot/js/boatIcons.test.js

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { makeBoatSvg, makeRadarSvg, shipTypeGlyph } from './boatIcons.js';

describe('vessel SVGs', () => {
    it('carry no CSS filter (contrast comes from an in-SVG halo)', () => {
        for (const svg of [makeBoatSvg('#f00', 30, true), makeBoatSvg('#0f0', 28, false, 'sail'),
                           makeRadarSvg('#00f', 26)]) {
            assert.ok(!svg.includes('filter'), svg);
            assert.ok(!svg.includes('style='), svg);
        }
    });

    it('draw the halo before (under) the coloured shape', () => {
        const svg = makeBoatSvg('#e76f51', 28, false);
        const halo = svg.indexOf('rgba(0,0,0,0.4)');
        const fill = svg.indexOf('fill="#e76f51"');
        assert.ok(halo > 0 && fill > halo);
    });

    it('size the viewBox around the centre so rotation pivots on the boat', () => {
        assert.ok(makeBoatSvg('#fff', 30, true).startsWith(
            '<svg width="30" height="30" viewBox="-15 -15 30 30">'));
    });

    it('add the ship-type glyph only for known categories', () => {
        assert.equal(shipTypeGlyph(undefined), '');
        assert.equal(shipTypeGlyph('unknown'), '');
        for (const c of ['sail', 'fish', 'commercial', 'service']) {
            assert.ok(makeBoatSvg('#fff', 28, false, c).includes(shipTypeGlyph(c)));
            assert.notEqual(shipTypeGlyph(c), '');
        }
    });
});
