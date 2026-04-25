// Tests for the radarLayer helpers exported via _internal. The render
// loop itself depends on Leaflet + Canvas + DOM and isn't unit-tested
// here -- the helpers are the policy decisions worth pinning.

import { test } from 'node:test';
import assert from 'node:assert/strict';

// radarLayer.js defines a CanvasGeoLayer via L.Layer.extend at module
// top-level; stub L before importing so the module loads under node.
// The stub never gets called from these tests (they only exercise
// _internal helpers), it just has to exist.
globalThis.L = { Layer: { extend: () => function () {} } };

const { _internal } = await import('../radarLayer.js');

const { shouldSuppressLowReturn, parseHexRgba, parseLegendColor } = _internal;

test('shouldSuppressLowReturn: normal pixel below mediumReturn is suppressed', () => {
    // Navico HALO ships mediumReturn = 5; bytes 1..4 are the blue
    // sea-clutter ramp the user wants gone.
    const legend = { mediumReturn: 5 };
    assert.equal(shouldSuppressLowReturn({ type: 'normal' }, 1, legend), true);
    assert.equal(shouldSuppressLowReturn({ type: 'normal' }, 4, legend), true);
});

test('shouldSuppressLowReturn: normal pixel at mediumReturn boundary is kept', () => {
    // The boundary itself is the start of "real" returns.
    const legend = { mediumReturn: 5 };
    assert.equal(shouldSuppressLowReturn({ type: 'normal' }, 5, legend), false);
});

test('shouldSuppressLowReturn: normal pixel above mediumReturn is kept', () => {
    const legend = { mediumReturn: 5 };
    assert.equal(shouldSuppressLowReturn({ type: 'normal' }, 10, legend), false);
});

test('shouldSuppressLowReturn: index 0 (no echo) is never suppressed', () => {
    // Byte 0 is the "no echo" marker -- already transparent in any
    // sane palette. Suppressing it would be a no-op but the rule is
    // worded "index >= 1" so a future change can't accidentally
    // include it.
    const legend = { mediumReturn: 5 };
    assert.equal(shouldSuppressLowReturn({ type: 'normal' }, 0, legend), false);
});

test('shouldSuppressLowReturn: doppler pixels keep their colour even in suppress band', () => {
    // Doppler-receding is rendered as a pale blue and lives below
    // mediumReturn on some palettes; we must NOT suppress it because
    // the user explicitly wants doppler markers visible.
    const legend = { mediumReturn: 5 };
    assert.equal(shouldSuppressLowReturn({ type: 'dopplerReceding' }, 2, legend), false);
    assert.equal(shouldSuppressLowReturn({ type: 'dopplerApproaching' }, 3, legend), false);
    assert.equal(shouldSuppressLowReturn({ type: 'history' }, 4, legend), false);
    assert.equal(shouldSuppressLowReturn({ type: 'targetBorder' }, 1, legend), false);
});

test('shouldSuppressLowReturn: nothing suppressed when legend lacks mediumReturn', () => {
    // Non-Navico providers may not ship mediumReturn at all; without
    // it we have no spec-grounded threshold so we suppress nothing.
    assert.equal(shouldSuppressLowReturn({ type: 'normal' }, 2, {}), false);
    assert.equal(shouldSuppressLowReturn({ type: 'normal' }, 2, null), false);
    assert.equal(shouldSuppressLowReturn({ type: 'normal' }, 2, { mediumReturn: 'five' }), false);
});

test('shouldSuppressLowReturn: null pixel returns false', () => {
    // Defensive: a malformed legend.pixels[] entry shouldn't crash
    // the legend setup -- it just renders transparent via the
    // existing parseLegendColor path.
    assert.equal(shouldSuppressLowReturn(null, 2, { mediumReturn: 5 }), false);
    assert.equal(shouldSuppressLowReturn(undefined, 2, { mediumReturn: 5 }), false);
});

test('parseLegendColor: hex string round-trips', () => {
    assert.deepEqual(parseLegendColor('#ff0000ff'), [255, 0, 0, 255]);
    assert.deepEqual(parseLegendColor('#00000000'), [0, 0, 0, 0]);
});

test('parseLegendColor: object form clamps to byte range', () => {
    assert.deepEqual(parseLegendColor({ r: 1, g: 2, b: 3, a: 4 }), [1, 2, 3, 4]);
    // Missing alpha defaults to opaque per spec.
    assert.deepEqual(parseLegendColor({ r: 5, g: 6, b: 7 }), [5, 6, 7, 255]);
});

test('parseLegendColor: null / unknown shape returns transparent', () => {
    // Shape mismatches must not throw; legend desync would otherwise
    // brick the entire overlay setup for one bad pixel.
    assert.deepEqual(parseLegendColor(null), [0, 0, 0, 0]);
    assert.deepEqual(parseLegendColor(undefined), [0, 0, 0, 0]);
    assert.deepEqual(parseLegendColor(42), [0, 0, 0, 0]);
});

test('parseHexRgba: short / malformed hex returns transparent', () => {
    assert.deepEqual(parseHexRgba(''), [0, 0, 0, 0]);
    assert.deepEqual(parseHexRgba('not a colour'), [0, 0, 0, 0]);
    assert.deepEqual(parseHexRgba('#abc'), [0, 0, 0, 0]);  // 3-digit not supported
});

const { wrapSpoke, headingToSpokeOffset } = _internal;

test('wrapSpoke: in-range index unchanged', () => {
    assert.equal(wrapSpoke(0, 2048), 0);
    assert.equal(wrapSpoke(1024, 2048), 1024);
    assert.equal(wrapSpoke(2047, 2048), 2047);
});

test('wrapSpoke: above-range index wraps', () => {
    // Wire schema is uint32 so this is mostly defensive against
    // angle + heading overflow than incoming values; pin it anyway.
    assert.equal(wrapSpoke(2048, 2048), 0);
    assert.equal(wrapSpoke(4096, 2048), 0);
    assert.equal(wrapSpoke(2050, 2048), 2);
});

test('wrapSpoke: negative index wraps to positive', () => {
    // The reason the helper exists: JS `%` preserves the sign of the
    // dividend, so `-1 % 2048` is -1, which would index out of bounds.
    assert.equal(wrapSpoke(-1, 2048), 2047);
    assert.equal(wrapSpoke(-2049, 2048), 2047);
});

test('headingToSpokeOffset: north-up returns 0', () => {
    assert.equal(headingToSpokeOffset(0, 2048), 0);
});

test('headingToSpokeOffset: full revolution wraps to spokes count', () => {
    // 2*pi rad maps to a full revolution; before wrapSpoke applies,
    // the offset itself can be the full count.
    assert.equal(headingToSpokeOffset(2 * Math.PI, 2048), 2048);
});

test('headingToSpokeOffset: quarter turn east is +N/4', () => {
    // 90deg starboard rotates the bow-relative angle by spokes/4 to
    // align onto north-up. Pin so a future sign-flip / 2pi swap
    // would visibly fail.
    assert.equal(headingToSpokeOffset(Math.PI / 2, 2048), 512);
});
