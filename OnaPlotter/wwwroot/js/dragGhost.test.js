// Tests for dragGhost.js: the ghost marker + dashed delta line shown
// while a draggable edit marker moves. Leaflet is stubbed with just
// the surface the helper touches.
//
// Run with: node --test OnaPlotter/wwwroot/js/dragGhost.test.js

import { describe, it, beforeEach } from 'node:test';
import assert from 'node:assert/strict';

function makeContainer() {
    const c = {
        layers: [],
        removed: [],
        removeLayer(l) { this.removed.push(l); this.layers = this.layers.filter(x => x !== l); },
    };
    return c;
}

function fakeLayer(kind, args) {
    return {
        kind, args,
        tooltip: null,
        latlngs: null,
        addTo(c) { this.container = c; c.layers.push(this); return this; },
        setLatLngs(v) { this.latlngs = v; },
        bindTooltip(text, o) { this.tooltip = { text, o, latlng: null }; return this; },
        openTooltip(ll) { this.tooltip.latlng = ll; return this; },
        setTooltipContent(t) { this.tooltip.text = t; },
        getTooltip() { return this.tooltip && { setLatLng: ll => { this.tooltip.latlng = ll; } }; },
    };
}

globalThis.L = {
    marker: (ll, o) => fakeLayer('marker', { ll, o }),
    polyline: (pts, o) => fakeLayer('polyline', { pts, o }),
    divIcon: o => ({ divIcon: o }),
};

const { attachDragGhost, formatDelta } = await import('./dragGhost.js');

function fakeMarker(ll) {
    const handlers = {};
    return {
        ll,
        on(ev, fn) { handlers[ev] = fn; },
        fire(ev) { handlers[ev]({ target: { getLatLng: () => this.ll } }); },
        handlers,
    };
}

describe('formatDelta', () => {
    it('shows whole metres under 1 km', () => {
        assert.equal(formatDelta(0), 'Δ 0 m');
        assert.equal(formatDelta(999.4), 'Δ 999 m');
    });
    it('switches to nautical miles at 1 km', () => {
        assert.equal(formatDelta(1852), 'Δ 1.00 nm');
        assert.equal(formatDelta(1000), 'Δ 0.54 nm');
    });
});

describe('attachDragGhost', () => {
    let container, marker, drags, ends, opts;
    beforeEach(() => {
        container = makeContainer();
        marker = fakeMarker({ lat: 10, lng: 20 });
        drags = [];
        ends = 0;
        opts = {
            container: () => container,
            color: () => '#abc',
            icon: { className: 'ghost-cls', html: '<i></i>', size: 24 },
            tooltipOffset: -18,
            onDrag: ll => { drags.push(ll); },
            onEnd: () => { ends++; },
        };
        attachDragGhost(marker, opts);
    });

    it('dragstart adds a ghost marker at the origin and a tooltip line', () => {
        marker.fire('dragstart');
        const [ghost, line] = container.layers;
        assert.equal(ghost.kind, 'marker');
        assert.deepEqual(ghost.args.ll, { lat: 10, lng: 20 });
        assert.equal(ghost.args.o.interactive, false);
        assert.equal(ghost.args.o.zIndexOffset, 500);
        assert.deepEqual(ghost.args.o.icon.divIcon,
            { className: 'ghost-cls', html: '<i></i>', iconSize: [24, 24], iconAnchor: [12, 12] });
        assert.equal(line.kind, 'polyline');
        assert.equal(line.args.o.color, '#abc');
        assert.equal(line.tooltip.text, 'Δ 0 m');
        assert.deepEqual(line.tooltip.o.offset, [0, -18]);
        assert.equal(line.tooltip.o.className, 'measure-tooltip');
    });

    it('drag calls onDrag and updates line, label and tooltip position', () => {
        marker.fire('dragstart');
        marker.ll = { lat: 10, lng: 20.0001 };
        marker.fire('drag');
        const line = container.layers[1];
        assert.equal(drags.length, 1);
        assert.deepEqual(line.latlngs, [{ lat: 10, lng: 20 }, marker.ll]);
        assert.match(line.tooltip.text, /^Δ \d+ m$/);
        assert.equal(line.tooltip.latlng, marker.ll);
    });

    it('onDrag returning false skips the ghost update', () => {
        opts.onDrag = () => false;
        const m = fakeMarker({ lat: 0, lng: 0 });
        attachDragGhost(m, opts);
        m.fire('dragstart');
        m.ll = { lat: 1, lng: 1 };
        m.fire('drag');
        assert.equal(container.layers.at(-1).latlngs, null);
    });

    it('drag before dragstart only calls onDrag', () => {
        marker.fire('drag');
        assert.equal(drags.length, 1);
        assert.equal(container.layers.length, 0);
    });

    it('dragend removes ghost and line, then calls onEnd', () => {
        marker.fire('dragstart');
        const [ghost, line] = container.layers;
        marker.fire('dragend');
        assert.deepEqual(container.removed, [ghost, line]);
        assert.equal(container.layers.length, 0);
        assert.equal(ends, 1);
    });

    it('dragend tolerates a container that vanished mid-drag', () => {
        marker.fire('dragstart');
        opts.container = () => null;
        // opts is read live so a torn-down layer is a no-op
        marker.fire('dragend');
        assert.equal(container.removed.length, 0);
        assert.equal(ends, 1);
    });

    it('works without onEnd', () => {
        delete opts.onEnd;
        marker.fire('dragstart');
        marker.fire('dragend');
        assert.equal(container.removed.length, 2);
    });
});
