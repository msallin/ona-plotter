// Tests for anchorLayer.js alarm-circle colouring.
// Run with: node --test OnaPlotter/wwwroot/js/anchorLayer.test.js

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';

const colors = { anchorOk: 'green', anchorDrag: 'red' };

// Minimal Leaflet stub: each vector layer records its last style so the
// test can read the colour the helm would see.
function stubLayer(latlng, opts) {
    const layer = {
        latlng: { lat: latlng[0], lng: latlng[1] },
        style: { ...opts },
        radius: opts?.radius ?? 0,
        addTo() { return layer; },
        setStyle(s) { Object.assign(layer.style, s); },
        getLatLng() { return layer.latlng; },
        setLatLng(ll) { layer.latlng = { lat: ll[0], lng: ll[1] }; },
        setLatLngs() {},
        getRadius() { return layer.radius; },
        setRadius(r) { layer.radius = r; },
        on() {},
    };
    return layer;
}

async function freshLayer() {
    const created = [];
    globalThis.L = {
        circleMarker: (ll, o) => { const l = stubLayer(ll, o); created.push(['marker', l]); return l; },
        circle: (ll, o) => { const l = stubLayer(ll, o); created.push(['circle', l]); return l; },
        polyline: (ll, o) => stubLayer([0, 0], o),
    };
    const mod = await import(`./anchorLayer.js?t=${Date.now()}_${Math.random()}`);
    mod.init({ removeLayer() {} }, { colors });
    const circle = () => created.filter(([k]) => k === 'circle').at(-1)[1];
    return { mod, circle };
}

// ~0.0009 deg of latitude is ~100 m north of the anchor.
const ANCHOR = [54.0, 11.0];
const INSIDE = [54.0002, 11.0];
const OUTSIDE = [54.0009, 11.0];

describe('anchor circle colour', () => {
    it('turns red when the boat leaves the circle', async () => {
        const { mod, circle } = await freshLayer();
        mod.setBoatPosition(...INSIDE);
        mod.setAnchor(...ANCHOR, 50);
        mod.setBoatPosition(...OUTSIDE);
        assert.equal(circle().style.color, 'red');
    });

    it('re-evaluates inside/outside when the incomplete state clears', async () => {
        // Helm drops the pin, the boat is already dragging outside the
        // circle while the radius dialog is open, then commits a radius.
        // The ring must show red straight away, not the anchorOk restyle.
        const { mod, circle } = await freshLayer();
        mod.setBoatPosition(...OUTSIDE);
        mod.setAnchor(...ANCHOR, 50);
        mod.setBoatPosition(...OUTSIDE);
        assert.equal(circle().style.color, 'red');
        mod.setAnchorIncomplete(true);
        assert.equal(circle().style.color, '#f59e0b');
        mod.setAnchorIncomplete(false);
        assert.equal(circle().style.color, 'red');
    });

    it('a later tick outside still restyles after the incomplete state clears', async () => {
        const { mod, circle } = await freshLayer();
        mod.setBoatPosition(...INSIDE);
        mod.setAnchor(...ANCHOR, 50);
        mod.setBoatPosition(...INSIDE);
        mod.setAnchorIncomplete(true);
        mod.setAnchorIncomplete(false);
        assert.equal(circle().style.color, 'green');
        mod.setBoatPosition(...OUTSIDE);
        assert.equal(circle().style.color, 'red');
    });

    it('shows green inside after the incomplete state clears', async () => {
        const { mod, circle } = await freshLayer();
        mod.setBoatPosition(...INSIDE);
        mod.setAnchor(...ANCHOR, 50);
        mod.setAnchorIncomplete(true);
        mod.setAnchorIncomplete(false);
        assert.equal(circle().style.color, 'green');
    });
});
