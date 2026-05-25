// Tests for radarProtobuf.js. Run via:
//   node --test OnaPlotter/wwwroot/js/__tests__/radarProtobuf.test.mjs
//
// .mjs so the file-extension picks ESM without a package.json, and
// under a dir the Blazor build pipeline doesn't copy (__tests__).
//
// The decoder is hand-rolled - every pin below exists because a
// field-type we actually ship was easy to get wrong:
//   * varint overflow past 28 bits (range can legitimately be up to
//     the full 32-bit width on some radars)
//   * double little-endian (DataView default is big-endian)
//   * bytes aliasing the underlying buffer vs. copying
//   * skipping unknown fields without wrecking the read position
//   * optional fields returning undefined, not a default zero

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { decodeRadarMessage } from '../radarProtobuf.js';

// --- Tiny hand-rolled encoder for test fixtures --------------------
// We don't want to pull in protobufjs just to assemble test bytes;
// the schema is small enough that encoding is a few lines per type.
// These helpers only cover the wire types decodeRadarMessage reads.

function encodeVarint(n) {
    const out = [];
    while (n > 0x7f) {
        out.push((n & 0x7f) | 0x80);
        n = Math.floor(n / 128);
    }
    out.push(n & 0x7f);
    return out;
}

function encodeBytes(bytes) {
    return [...encodeVarint(bytes.length), ...bytes];
}

function encodeDouble(d) {
    const buf = new ArrayBuffer(8);
    new DataView(buf).setFloat64(0, d, true);
    return [...new Uint8Array(buf)];
}

// Spoke fields: 1=angle(varint), 2=bearing(varint), 3=range(varint),
// 4=time(varint), 5=data(bytes), 6=lat(double), 7=lon(double).
// tag = (field << 3) | wire; 0=varint, 1=64fixed, 2=lendelim.
function encodeSpoke({ angle, bearing, range, time, data, lat, lon }) {
    const out = [];
    if (angle !== undefined) out.push((1 << 3) | 0, ...encodeVarint(angle));
    if (bearing !== undefined) out.push((2 << 3) | 0, ...encodeVarint(bearing));
    if (range !== undefined) out.push((3 << 3) | 0, ...encodeVarint(range));
    if (time !== undefined) out.push((4 << 3) | 0, ...encodeVarint(time));
    if (data !== undefined) out.push((5 << 3) | 2, ...encodeBytes(data));
    if (lat !== undefined) out.push((6 << 3) | 1, ...encodeDouble(lat));
    if (lon !== undefined) out.push((7 << 3) | 1, ...encodeDouble(lon));
    return out;
}

// Top-level: field 2 = repeated spokes (lendelim).
function encodeMessage(spokes) {
    const out = [];
    for (const s of spokes) {
        const body = encodeSpoke(s);
        out.push((2 << 3) | 2, ...encodeVarint(body.length), ...body);
    }
    return new Uint8Array(out);
}

// --- Tests ----------------------------------------------------------

test('empty message yields no spokes', () => {
    const msg = decodeRadarMessage(new Uint8Array());
    assert.deepEqual(msg.spokes, []);
});

test('single spoke, all required fields only', () => {
    const bytes = encodeMessage([{ angle: 42, range: 2000, data: [1, 2, 3] }]);
    const msg = decodeRadarMessage(bytes);
    assert.equal(msg.spokes.length, 1);
    const s = msg.spokes[0];
    assert.equal(s.angle, 42);
    assert.equal(s.range, 2000);
    assert.equal(s.bearing, undefined);
    assert.equal(s.lat, undefined);
    assert.equal(s.lon, undefined);
    assert.deepEqual([...s.data], [1, 2, 3]);
});

test('spoke with bearing + lat/lon', () => {
    const bytes = encodeMessage([{
        angle: 500, bearing: 1024, range: 3000,
        data: [0, 0, 5, 15],
        lat: 47.4,
        lon: -122.5,
    }]);
    const [s] = decodeRadarMessage(bytes).spokes;
    assert.equal(s.angle, 500);
    assert.equal(s.bearing, 1024);
    assert.equal(s.range, 3000);
    assert.ok(Math.abs(s.lat - 47.4) < 1e-12);
    assert.ok(Math.abs(s.lon - (-122.5)) < 1e-12);
    assert.deepEqual([...s.data], [0, 0, 5, 15]);
});

test('spoke with time field - skipped, other fields intact', () => {
    // time is a uint64 varint we deliberately ignore. Verify that
    // skipping it doesn't corrupt the read position for fields that
    // follow.
    const bytes = encodeMessage([{
        angle: 1, range: 100, time: 1735689600000, // 2025-01-01 ms
        data: [7, 8, 9],
    }]);
    const [s] = decodeRadarMessage(bytes).spokes;
    assert.equal(s.angle, 1);
    assert.equal(s.range, 100);
    assert.deepEqual([...s.data], [7, 8, 9]);
});

test('multi-spoke message decoded in order', () => {
    const bytes = encodeMessage([
        { angle: 0, range: 1000, data: [1] },
        { angle: 1, range: 1000, data: [2] },
        { angle: 2, range: 1000, data: [3] },
    ]);
    const msg = decodeRadarMessage(bytes);
    assert.equal(msg.spokes.length, 3);
    assert.deepEqual(msg.spokes.map(s => s.angle), [0, 1, 2]);
    assert.deepEqual(msg.spokes.map(s => [...s.data][0]), [1, 2, 3]);
});

test('large varint past 28 bits decodes without sign flip', () => {
    // range can legitimately exceed 2^28 = 268 million (would only
    // happen with absurd values, but this is the guard). Pick a
    // value that exercises the multiplication branch in Reader.varint.
    const big = 0xF0000000; // 4,026,531,840 - well into 32-bit unsigned range
    const bytes = encodeMessage([{ angle: 0, range: big, data: [0] }]);
    const [s] = decodeRadarMessage(bytes).spokes;
    assert.equal(s.range, big);
    assert.ok(s.range > 0, 'range must not flip negative');
});

test('double fields decode as little-endian', () => {
    // Regression: DataView.getFloat64 defaults to big-endian. Ensure
    // the decoder passes the little-endian flag.
    const lat = 52.37019512;
    const bytes = encodeMessage([{ angle: 0, range: 100, data: [0], lat }]);
    const [s] = decodeRadarMessage(bytes).spokes;
    assert.ok(Math.abs(s.lat - lat) < 1e-12, `got ${s.lat}, expected ${lat}`);
});

test('empty data bytes ok', () => {
    // Zero-range spoke - happens during startup / range change.
    const bytes = encodeMessage([{ angle: 0, range: 0, data: [] }]);
    const [s] = decodeRadarMessage(bytes).spokes;
    assert.equal(s.data.length, 0);
});

test('bytes field returns a view, not a copy', () => {
    // We care about this for painting perf: the spoke handler reads
    // thousands of bytes per frame and we don't want to allocate a
    // new Uint8Array every time. Verify the returned view points
    // into the original buffer.
    const bytes = encodeMessage([{ angle: 0, range: 10, data: [1, 2, 3, 4] }]);
    const [s] = decodeRadarMessage(bytes).spokes;
    assert.strictEqual(s.data.buffer, bytes.buffer);
});

test('unknown top-level field is skipped silently', () => {
    // Forward-compat: pretend server adds a top-level field 7
    // (varint). Our decoder should skip it and still surface the
    // real spokes.
    const extra = [(7 << 3) | 0, ...encodeVarint(999)];
    const real = encodeMessage([{ angle: 5, range: 500, data: [9] }]);
    const combined = new Uint8Array([...extra, ...real]);
    const msg = decodeRadarMessage(combined);
    assert.equal(msg.spokes.length, 1);
    assert.equal(msg.spokes[0].angle, 5);
});

test('unknown spoke field is skipped silently', () => {
    // Same but inside a Spoke: field 9, 64-bit fixed.
    // Spoke body: (1<<3)|0, 5, then (9<<3)|1, 8 random bytes.
    const spokeBody = [
        (1 << 3) | 0, ...encodeVarint(5),
        (9 << 3) | 1, 1, 2, 3, 4, 5, 6, 7, 8,
        (3 << 3) | 0, ...encodeVarint(1000),
    ];
    const bytes = new Uint8Array([
        (2 << 3) | 2, ...encodeVarint(spokeBody.length), ...spokeBody,
    ]);
    const [s] = decodeRadarMessage(bytes).spokes;
    assert.equal(s.angle, 5);
    assert.equal(s.range, 1000);
});

test('malformed varint throws rather than spinning forever', () => {
    // 10 bytes all with high bit set would otherwise loop; decoder
    // bails at >63 shift.
    const bytes = new Uint8Array([(1 << 3) | 0, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
    // wrap in a Spoke so the top-level parser picks it up
    const wrapped = new Uint8Array([(2 << 3) | 2, bytes.length, ...bytes]);
    assert.throws(() => decodeRadarMessage(wrapped), /varint/);
});

test('truncated bytes field throws, not silent corruption', () => {
    // Declare len=100 but only have 5 actual bytes.
    const body = [(5 << 3) | 2, ...encodeVarint(100), 1, 2, 3, 4, 5];
    const bytes = new Uint8Array([
        (2 << 3) | 2, ...encodeVarint(body.length), ...body,
    ]);
    assert.throws(() => decodeRadarMessage(bytes), /ran off end/);
});

test('truncated double field throws our error, not RangeError', () => {
    // Spoke body declares lat (wire 1) at the end, then only 3 bytes.
    // Previously DataView.getFloat64 would surface a RangeError; we
    // want the uniform "ran off end" failure mode.
    const spokeBody = [
        (1 << 3) | 0, ...encodeVarint(5),
        (6 << 3) | 1, 1, 2, 3,     // lat starts but only 3 of 8 bytes
    ];
    const bytes = new Uint8Array([
        (2 << 3) | 2, ...encodeVarint(spokeBody.length), ...spokeBody,
    ]);
    assert.throws(() => decodeRadarMessage(bytes), /double ran off end/);
});

test('repeat decode calls reuse the Reader without leaking state', () => {
    // Perf: the module-level scratch Reader is reused across calls,
    // and Spoke objects are now pooled too. Pin both:
    //   - Reader: a second call decodes the second buffer correctly
    //     (no leftover offset / view from the first).
    //   - Spoke pool: each call's Spoke ref is the pool's slot, so
    //     a holder of `ma.spokes[0]` past the next decodeRadarMessage
    //     call observes the next call's values. Captures snapshot
    //     fields IMMEDIATELY AFTER each decode, before the pool is
    //     reused. Production callers (_onFrame) consume spokes
    //     synchronously inside the same call, so this contract is
    //     fine; the explicit snapshot here pins it.
    const a = encodeMessage([{ angle: 1, range: 10, data: [1] }]);
    const ma = decodeRadarMessage(a);
    const angle1 = ma.spokes[0].angle;
    const data1 = [...ma.spokes[0].data];

    const b = encodeMessage([{ angle: 99, range: 1000, data: [9] }]);
    const mb = decodeRadarMessage(b);
    const angle2 = mb.spokes[0].angle;
    const data2 = [...mb.spokes[0].data];

    assert.equal(angle1, 1);
    assert.equal(angle2, 99);
    assert.deepEqual(data1, [1]);
    assert.deepEqual(data2, [9]);
});

test('Spoke pool reuses the same object instance across calls', () => {
    // Pin the contract: identical-shape decodes return the same
    // pooled Spoke instance. Production caller relies on this for
    // GC pressure relief. A regression that switches back to fresh
    // literals would re-introduce ~1k allocs/sec on a typical
    // recreational radar.
    const a = encodeMessage([{ angle: 1, range: 10, data: [1] }]);
    const ma = decodeRadarMessage(a);
    const firstRef = ma.spokes[0];
    const b = encodeMessage([{ angle: 2, range: 20, data: [2] }]);
    const mb = decodeRadarMessage(b);
    const secondRef = mb.spokes[0];
    assert.equal(firstRef === secondRef, true);
});

test('pool slots above current count release their WS-buffer pins', () => {
    // The pool is module-level + high-water-mark sized. Without an
    // explicit release, a 4-spoke frame followed by a 1-spoke frame
    // leaves slots [1..3] still pointing at the 4-spoke frame's
    // data Uint8Arrays - and each view pins the entire WS ArrayBuffer
    // alive until the next time that slot is refilled. Over a
    // long-running session with variable spoke counts that's a
    // steady-state leak the GC can't drain. Pin the fix: after a
    // lower-count decode the higher slots' `data` MUST be the empty
    // sentinel.
    //
    // We can't reach the pool directly (it's module-private), but
    // the test "reuses the same object instance" already showed that
    // pool slot N is the same object reference as spokes[N] in the
    // most recent decode of size >= N+1. So: capture refs from a
    // 4-spoke decode, run a 1-spoke decode, and assert the captured
    // refs for indices 1..3 now hold EMPTY_BYTES.
    const big = encodeMessage([
        { angle: 0, range: 100, data: [10, 11, 12] },
        { angle: 1, range: 100, data: [20, 21, 22] },
        { angle: 2, range: 100, data: [30, 31, 32] },
        { angle: 3, range: 100, data: [40, 41, 42] },
    ]);
    const mBig = decodeRadarMessage(big);
    const tailRefs = [mBig.spokes[1], mBig.spokes[2], mBig.spokes[3]];
    // Sanity: before the second decode the tail still holds the
    // 4-spoke frame's data. (If this is ever false the pool
    // wasn't actually populated and the rest of the test proves
    // nothing.)
    for (const s of tailRefs) assert.ok(s.data.length > 0);

    const small = encodeMessage([{ angle: 99, range: 100, data: [99] }]);
    decodeRadarMessage(small);

    // After the lower-count decode, pool slots 1..3 must have
    // released their data references back to the empty sentinel
    // (length 0). Same length AND same identity are both checked -
    // identity is the stronger guarantee that the release used the
    // shared EMPTY_BYTES rather than allocating a fresh empty array
    // per slot.
    for (const s of tailRefs) {
        assert.equal(s.data.length, 0, 'data not released to empty');
    }
    // All three released slots should share one EMPTY_BYTES instance,
    // which they should also share with each other.
    assert.strictEqual(tailRefs[0].data, tailRefs[1].data);
    assert.strictEqual(tailRefs[1].data, tailRefs[2].data);
});
