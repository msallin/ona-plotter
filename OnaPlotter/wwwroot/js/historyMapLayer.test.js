// Tests for historyMapLayer.js pure helpers.
// Run with: node --test OnaPlotter/wwwroot/js/historyMapLayer.test.js

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { resolveColor, nearestIndex, depthTooltip, frameThrottle } from './historyMapLayer.js';

const tokens = { '--map-own': ' #ff6f6f ', '--empty': '' };
const readVar = (name) => tokens[name] ?? '';

describe('resolveColor', () => {
    it('passes a plain colour through', () => {
        assert.equal(resolveColor('#a78bfa', readVar), '#a78bfa');
        assert.equal(resolveColor('rgb(1,2,3)', readVar), 'rgb(1,2,3)');
    });

    it('resolves a defined token and trims it', () => {
        assert.equal(resolveColor('var(--map-own, #ec4899)', readVar), '#ff6f6f');
    });

    it('falls back when the token is empty or undefined', () => {
        assert.equal(resolveColor('var(--empty, #94a3b8)', readVar), '#94a3b8');
        assert.equal(resolveColor('var(--missing, #fbbf24)', readVar), '#fbbf24');
    });

    it('returns empty for an undefined token without fallback', () => {
        assert.equal(resolveColor('var(--missing)', readVar), '');
    });

    it('tolerates whitespace inside var()', () => {
        assert.equal(resolveColor('var( --map-own ,  #000 )', readVar), '#ff6f6f');
    });

    it('returns empty for non-string input', () => {
        assert.equal(resolveColor(undefined, readVar), '');
        assert.equal(resolveColor(null, readVar), '');
    });
});

describe('nearestIndex', () => {
    it('returns -1 for no coordinates', () => {
        assert.equal(nearestIndex([], 47, 8), -1);
    });

    it('returns the only index for one coordinate', () => {
        assert.equal(nearestIndex([[47, 8]], 0, 0), 0);
    });

    it('picks the closest point on a realistic passage', () => {
        // Hover near the middle fix of a three-fix leg across a lake.
        const leg = [[47.30, 8.55], [47.25, 8.60], [47.20, 8.65]];
        assert.equal(nearestIndex(leg, 47.251, 8.601), 1);
        assert.equal(nearestIndex(leg, 47.31, 8.54), 0);
        assert.equal(nearestIndex(leg, 47.1, 8.7), 2);
    });

    it('keeps the first index on an exact tie', () => {
        assert.equal(nearestIndex([[0, 1], [0, -1]], 0, 0), 0);
    });
});

describe('depthTooltip', () => {
    it('appends the depth at the index with one decimal', () => {
        assert.equal(depthTooltip('<b>Moving</b>', [3.14, 5], 0), '<b>Moving</b><br>Depth here: 3.1 m');
    });

    it('keeps the static tooltip when there is no sample', () => {
        assert.equal(depthTooltip('t', [null, 5], 0), 't');
        assert.equal(depthTooltip('t', [undefined], 0), 't');
        assert.equal(depthTooltip('t', null, 0), 't');
        assert.equal(depthTooltip('t', [5], -1), 't');
        assert.equal(depthTooltip('t', [NaN], 0), 't');
    });

    it('renders a zero depth instead of hiding it', () => {
        assert.equal(depthTooltip('t', [0], 0), 't<br>Depth here: 0.0 m');
    });
});

describe('frameThrottle', () => {
    // Manual frame clock standing in for requestAnimationFrame.
    function frames() {
        let queue = new Map();
        let next = 1;
        return {
            schedule: (cb) => { queue.set(next, cb); return next++; },
            unschedule: (id) => queue.delete(id),
            tick() { const q = queue; queue = new Map(); for (const cb of q.values()) cb(); },
        };
    }

    it('runs once per frame with the latest arguments', () => {
        const clock = frames();
        const calls = [];
        const t = frameThrottle((x) => calls.push(x), clock.schedule, clock.unschedule);
        t(1); t(2); t(3);
        assert.deepEqual(calls, []);
        clock.tick();
        assert.deepEqual(calls, [3]);
        t(4);
        clock.tick();
        assert.deepEqual(calls, [3, 4]);
    });

    it('does nothing on a frame without calls', () => {
        const clock = frames();
        const calls = [];
        frameThrottle((x) => calls.push(x), clock.schedule, clock.unschedule);
        clock.tick();
        assert.deepEqual(calls, []);
    });

    it('cancel drops the pending call', () => {
        const clock = frames();
        const calls = [];
        const t = frameThrottle((x) => calls.push(x), clock.schedule, clock.unschedule);
        t(1);
        t.cancel();
        clock.tick();
        assert.deepEqual(calls, []);
        t(2);
        clock.tick();
        assert.deepEqual(calls, [2]);
    });
});
