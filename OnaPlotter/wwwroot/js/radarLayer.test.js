// Tests for the radar overlay's socket lifecycle: teardown and
// pause / resume on page visibility.
// Run with: node --test OnaPlotter/wwwroot/js/radarLayer.test.js

import { test } from 'node:test';
import assert from 'node:assert/strict';

globalThis.L = { Layer: { extend: () => function () {} } };

class FakeSocket {
    static all = [];
    constructor(url) {
        this.url = url;
        this.closed = false;
        this.listeners = {};
        FakeSocket.all.push(this);
    }
    addEventListener(type, fn) { (this.listeners[type] ??= []).push(fn); }
    close() {
        this.closed = true;
        for (const fn of this.listeners.close ?? []) fn();
    }
}
globalThis.WebSocket = FakeSocket;
globalThis.document = {
    createElement: () => ({
        getContext: () => ({
            createImageData: (w, h) => ({ data: new Uint8ClampedArray(w * h * 4) }),
            clearRect() {},
        }),
    }),
};

const radar = await import('./radarLayer.js');
const map = { on() {}, off() {}, getPanes: () => ({}) };
const cfg = (id) => ({
    radarId: id, spokeDataUrl: `ws://radar/${id}`,
    spokesPerRevolution: 8, maxSpokeLength: 4, range: 1000,
});
const open = () => FakeSocket.all.filter(s => !s.closed);

test('teardown closes every spoke socket', () => {
    FakeSocket.all = [];
    radar.enableRadarOverlay({ map }, cfg('a'));
    radar.enableRadarOverlay({ map }, cfg('b'));
    assert.equal(open().length, 2);
    radar.tearDownAllRadarOverlays();
    assert.equal(open().length, 0);
    assert.equal(radar.getActiveRadarCount(), 0);
});

test('pause closes sockets without scheduling a reconnect; resume reopens', () => {
    FakeSocket.all = [];
    radar.enableRadarOverlay({ map }, cfg('a'));
    radar.setRadarPaused(true);
    assert.equal(open().length, 0);
    assert.equal(FakeSocket.all.length, 1, 'no reconnect while paused');
    radar.setRadarPaused(false);
    assert.equal(open().length, 1);
    assert.equal(FakeSocket.all.length, 2);
    radar.tearDownAllRadarOverlays();
});

test('an overlay enabled while hidden waits for resume', () => {
    FakeSocket.all = [];
    radar.setRadarPaused(true);
    radar.enableRadarOverlay({ map }, cfg('a'));
    assert.equal(FakeSocket.all.length, 0);
    radar.setRadarPaused(false);
    assert.equal(open().length, 1);
    radar.tearDownAllRadarOverlays();
});

test('resume is idempotent', () => {
    FakeSocket.all = [];
    radar.enableRadarOverlay({ map }, cfg('a'));
    radar.setRadarPaused(false);
    radar.setRadarPaused(false);
    assert.equal(FakeSocket.all.length, 1);
    radar.tearDownAllRadarOverlays();
});
