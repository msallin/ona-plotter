// Tests for the radarLayer helpers exported via _internal. The render
// loop itself depends on Leaflet + Canvas + DOM and isn't unit-tested
// here - the helpers are the policy decisions worth pinning.

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
    // A typical recreational radar ships mediumReturn = 5; bytes 1..4
    // are the blue sea-clutter ramp the user wants gone (metadata path).
    const legend = { mediumReturn: 5 };
    assert.equal(shouldSuppressLowReturn({ type: 'normal', color: '#000033ff' }, 1, legend), true);
    assert.equal(shouldSuppressLowReturn({ type: 'normal', color: '#0000ccff' }, 4, legend), true);
});

test('shouldSuppressLowReturn: blue-dominant normal pixel above mediumReturn is also suppressed', () => {
    // Common radar palettes keep painting blue-tinged normals above the
    // mediumReturn metadata cutoff (bytes 5-7 are #0000ff / #0033cc /
    // #006699). The colour check is the user-facing fix: anything
    // that LOOKS blue gets dropped regardless of intensity class.
    const legend = { mediumReturn: 5 };
    assert.equal(shouldSuppressLowReturn({ type: 'normal', color: '#0000ffff' }, 5, legend), true);
    assert.equal(shouldSuppressLowReturn({ type: 'normal', color: '#0033ccff' }, 6, legend), true);
    assert.equal(shouldSuppressLowReturn({ type: 'normal', color: '#006699ff' }, 7, legend), true);
});

test('shouldSuppressLowReturn: green-dominant normal pixel above mediumReturn is kept', () => {
    // Bytes 8+ on common palettes transition to green-dominant
    // (#009966, #00cc33, #00ff00, ...) - those are real targets
    // and must stay visible.
    const legend = { mediumReturn: 5 };
    assert.equal(shouldSuppressLowReturn({ type: 'normal', color: '#009966ff' }, 8, legend), false);
    assert.equal(shouldSuppressLowReturn({ type: 'normal', color: '#00cc33ff' }, 9, legend), false);
    assert.equal(shouldSuppressLowReturn({ type: 'normal', color: '#00ff00ff' }, 10, legend), false);
});

test('shouldSuppressLowReturn: red / yellow normals are kept', () => {
    const legend = { mediumReturn: 5 };
    assert.equal(shouldSuppressLowReturn({ type: 'normal', color: '#ffff00ff' }, 13, legend), false);
    assert.equal(shouldSuppressLowReturn({ type: 'normal', color: '#ff0000ff' }, 15, legend), false);
});

test('shouldSuppressLowReturn: index 0 (no echo) is never suppressed', () => {
    // Byte 0 is the "no echo" marker - already transparent in any
    // sane palette. The metadata-path is gated on index >= 1 so the
    // rule can't include it. The colour-path could otherwise - a
    // pixel of #000088ff at index 0 would be blue-dominant - so
    // pin both gating cases.
    const legend = { mediumReturn: 5 };
    assert.equal(shouldSuppressLowReturn({ type: 'normal', color: '#00000000' }, 0, legend), false);
});

test('shouldSuppressLowReturn: doppler / history / target border kept regardless of colour', () => {
    // Doppler-receding is rendered as a pale blue (`#90d0f0`) and
    // would otherwise be caught by the colour check; we explicitly
    // gate on type === 'normal' so semantic markers stay visible.
    const legend = { mediumReturn: 5 };
    assert.equal(shouldSuppressLowReturn({ type: 'dopplerReceding', color: '#90d0f0ff' }, 18, legend), false);
    assert.equal(shouldSuppressLowReturn({ type: 'dopplerApproaching', color: '#0000ffff' }, 17, legend), false);
    assert.equal(shouldSuppressLowReturn({ type: 'history', color: '#0000ffff' }, 19, legend), false);
    assert.equal(shouldSuppressLowReturn({ type: 'targetBorder', color: '#0000ffff' }, 16, legend), false);
});

test('shouldSuppressLowReturn: colour check still fires when legend lacks mediumReturn', () => {
    // A provider that doesn't ship mediumReturn but does paint sea
    // clutter as blue still gets the noise cleaned up via the colour-
    // only path.
    assert.equal(shouldSuppressLowReturn({ type: 'normal', color: '#0000ffff' }, 5, {}), true);
    assert.equal(shouldSuppressLowReturn({ type: 'normal', color: '#00ff00ff' }, 5, {}), false);
});

test('shouldSuppressLowReturn: null pixel returns false', () => {
    // Defensive: a malformed legend.pixels[] entry shouldn't crash
    // the legend setup - it just renders transparent via the
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

// --- computeSpokeIndex policy --------------------------------------------
//
// Pins the post-2026-05 default behaviour. Live boat at 192° true with
// Mayara as the radar provider showed bearing-angle = ~0 (bow-relative)
// on the wire instead of the spec's true-north reading, so spokes
// painted bow-up regardless of actual heading and the only times the
// picture looked correct were when the boat happened to point near
// north. Default path now composes angle + heading and treats the wire
// bearing as untrusted; the opt-in path stays available for installs
// whose provider verifiably emits true-north.

const { computeSpokeIndex } = _internal;

test('computeSpokeIndex default: ignores wire bearing, uses angle + heading', () => {
    // Boat pointing east (90deg) -> heading offset = spokes/4 = 512.
    // A bow-relative angle 0 should index 512 (east on the north-up
    // canvas) even though the wire claims bearing 0 (north). Default
    // useWireBearing=false rejects the wire's claim.
    const spoke = { angle: 0, bearing: 0 };
    assert.equal(computeSpokeIndex(spoke, Math.PI / 2, 2048, false), 512);
});

test('computeSpokeIndex default: bearing absent on the wire still uses angle + heading', () => {
    // Wire shape where bearing was never populated (undefined). Path
    // must still compose - this is the SK-spec-compliant "angle only"
    // shape and was already supported.
    const spoke = { angle: 100 };
    assert.equal(computeSpokeIndex(spoke, 0, 2048, false), 100);
});

test('computeSpokeIndex default: Mayara-style bearing=angle gets correct rotation', () => {
    // Reproduces the live wire diagnostic from openplotter.local: every
    // spoke carries bearing approximately equal to angle (radar HS is
    // ~0). At the boat's true heading 192deg a wire angle of 0 must
    // paint at the boat's heading offset (1092 on a 2048-spoke radar,
    // i.e. round(192/360 * 2048) = round(1092.27)) so the spoke lands
    // roughly true-south on the canvas. Pre-fix, the bearing-wins
    // branch put it at 0 (paint due north) and the user saw the
    // overlay rotated backwards on every heading.
    const spoke = { angle: 0, bearing: 0 };
    const heading192 = 192 * Math.PI / 180;
    assert.equal(computeSpokeIndex(spoke, heading192, 2048, false), 1092);
});

test('computeSpokeIndex opt-in: useWireBearing=true reads bearing directly', () => {
    // Helm has flipped the opt-in (provider verifiably emits
    // true-north). Wire bearing 512 means due east; the painter
    // indexes there regardless of the boat's heading.
    const spoke = { angle: 0, bearing: 512 };
    assert.equal(computeSpokeIndex(spoke, Math.PI, 2048, true), 512);
});

test('computeSpokeIndex opt-in: bearing absent on the wire falls back to angle + heading', () => {
    // Even with useWireBearing on, a spoke that omits the bearing field
    // must still paint correctly via the angle path. The provider can
    // emit bearing inconsistently (e.g. only on every Nth spoke) and
    // the overlay can't refuse to paint between them.
    const spoke = { angle: 0 };
    assert.equal(computeSpokeIndex(spoke, Math.PI / 2, 2048, true), 512);
});

test('computeSpokeIndex: negative compound index wraps to positive', () => {
    // Defensive: a spoke with angle=10 and a slight-negative heading
    // (e.g. a provider that sends headings as signed degrees and an
    // ill-timed wrap) would otherwise hit a negative LUT index.
    const spoke = { angle: 10 };
    assert.equal(computeSpokeIndex(spoke, -Math.PI / 2, 2048, false), 1546);
});

test('computeSpokeIndex: bearingAlignment rotates default path by N spokes', () => {
    // Installation-time radar antenna offset: when the helm mounts
    // the radar with antenna 0° not exactly toward the bow (common
    // on a pole/arch install), Mayara exposes a `bearingAlignment`
    // control they tune at commissioning. We read it at enable time
    // and add it to the spoke index so the picture lines up with
    // the chart regardless of mount orientation. 5° alignment on a
    // 2048-spoke radar = round(5/360 * 2048) = 28 spokes.
    const spoke = { angle: 0 };
    assert.equal(computeSpokeIndex(spoke, 0, 2048, false, 28), 28);
});

test('computeSpokeIndex: bearingAlignment also applied on opt-in path', () => {
    // Mayara doesn't bake bearingAlignment into its wire `bearing`
    // field (observed on the HALO 31 install where bearing == angle
    // on the wire while bearingAlignment was set to 5°). Apply it
    // on both paths so flipping the opt-in doesn't suddenly stop
    // compensating for the antenna offset.
    const spoke = { angle: 0, bearing: 100 };
    assert.equal(computeSpokeIndex(spoke, 0, 2048, true, 28), 128);
});

test('computeSpokeIndex: bearingAlignment default 0 reproduces unaligned behaviour', () => {
    // Backwards-compat: callers that don't pass the alignment param
    // get the old behaviour. Pins so a future refactor that flips
    // the default to a non-zero value can't silently drift the
    // picture by ~5° on every helm.
    const spoke = { angle: 100 };
    assert.equal(computeSpokeIndex(spoke, 0, 2048, false), 100);
});

