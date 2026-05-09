// Minimal protobuf decoder for the Signal K Radar spoke stream.
//
// The wire schema is tiny and stable (per radar_api.md v3.1):
//
//   message RadarMessage {
//     message Spoke {
//       uint32 angle = 1;           // [0..spokesPerRevolution), from bow clockwise
//       optional uint32 bearing = 2; // [0..spokesPerRevolution), from true north
//       uint32 range = 3;            // metres to last pixel
//       optional uint64 time = 4;    // millis since epoch
//       bytes data = 5;              // one byte per pixel, legend-indexed
//       optional double lat = 6;     // radar position at generation
//       optional double lon = 7;
//     }
//     repeated Spoke spokes = 2;
//   }
//
// Pulling in a full protobuf runtime for this is unjustified: the
// above fits in ~150 lines of hand-written decode. Only the wire
// types we actually use are implemented (varint, 64-bit fixed,
// length-delimited). If the schema grows we extend here.
//
// Usage:
//   const msg = decodeRadarMessage(new Uint8Array(arrayBuffer));
//   for (const spoke of msg.spokes) { ... }

// Module-level scratch + sentinel hoisted to the top so ESLint's
// no-use-before-define is satisfied. Both are referenced inside
// function bodies that fire only at runtime, after this module has
// finished evaluating; the previous "declare-where-it-feels-natural"
// layout was static-analysis-noisy without buying any runtime
// safety. _scratchReader stays null until the first decode call
// `??=` initialises it - the comment that used to live next to
// the declaration explained why a let-then-init pattern was used:
// eager `new Reader(...)` here would TDZ-trip the class declaration
// further down in the file.
let _scratchReader = null;
const EMPTY_BYTES = new Uint8Array(0);

// Spoke object pool. Decoded Spoke literals are hot-allocated -
// a typical recreational radar emits ~1k spokes/sec, each previously
// a fresh `{ angle, bearing, range, data, lat, lon }` literal at decode
// time. The pool keeps a high-water-mark array of pre-shaped
// objects and decodeSpokeInto resets fields in place so V8's
// hidden class stays stable across frames + the GC has nothing
// to sweep.
//
// Concurrency: JS event loop is single-threaded; the pool can
// be shared across overlays. The decoded result array
// (`_spokeResultArr`) must NOT be held past the `_onFrame` call
// that produced it - the next decode reuses the slot. Today's
// only caller (RadarOverlay._onFrame) consumes the array
// synchronously inside the same function, so this is safe.
const _spokePool = [];
const _spokeResultArr = [];

/**
 * Decodes a RadarMessage from a Uint8Array. Unknown fields are
 * skipped silently (forward-compat with server versions that add
 * fields we don't know about).
 *
 * A module-level Reader is reused across calls so the DataView +
 * Reader instance allocation cost doesn't scale with the spoke-
 * frame rate (up to ~50 frames / sec on a typical recreational radar).
 *
 * @param {Uint8Array} bytes
 * @returns {{ spokes: Array<Spoke> }}
 */
export function decodeRadarMessage(bytes) {
    const r = (_scratchReader ??= new Reader(new Uint8Array(1))).reset(bytes);
    let count = 0;
    while (!r.atEnd()) {
        const tag = r.varint();
        const field = tag >>> 3;
        const wire = tag & 7;
        if (field === 2 && wire === 2) {
            // Repeated Spoke: length-prefixed embedded message.
            const len = r.varint();
            const end = r.offset + len;
            // Acquire a pooled Spoke; grow the pool on first sight
            // of a higher count. Reset-in-place keeps V8's hidden
            // class stable across frames (vs. a fresh literal each
            // time, which the GC then has to sweep).
            let s = _spokePool[count];
            if (s === undefined) {
                s = { angle: 0, bearing: undefined, range: 0, data: EMPTY_BYTES, lat: undefined, lon: undefined };
                _spokePool[count] = s;
            }
            decodeSpokeInto(r, end, s);
            count++;
        } else {
            r.skip(wire);
        }
    }
    // Resize the result array (also reused) to exactly `count` and
    // populate refs from the pool. Two array writes per spoke;
    // negligible vs. the saved per-spoke literal alloc.
    _spokeResultArr.length = count;
    for (let i = 0; i < count; i++) _spokeResultArr[i] = _spokePool[i];
    return { spokes: _spokeResultArr };
}

/**
 * @typedef {Object} Spoke
 * @property {number} angle
 * @property {number} [bearing]
 * @property {number} range
 * @property {Uint8Array} data
 * @property {number} [lat]
 * @property {number} [lon]
 */

/**
 * Decode-into a pooled Spoke object. Resets every field first so
 * the object's hidden class stays stable across frames (V8 keys
 * its hidden class on the property-set order, not on the values;
 * setting all six fields the same way every call keeps the shape
 * monomorphic). EMPTY_BYTES + undefined are the documented
 * absent-value sentinels.
 *
 * @param {Reader} r
 * @param {number} end
 * @param {Spoke} s
 */
function decodeSpokeInto(r, end, s) {
    s.angle = 0;
    s.bearing = undefined;
    s.range = 0;
    s.data = EMPTY_BYTES;
    s.lat = undefined;
    s.lon = undefined;
    while (r.offset < end) {
        const tag = r.varint();
        const field = tag >>> 3;
        const wire = tag & 7;
        switch (field) {
            case 1: s.angle = r.varint(); break;           // wire 0
            case 2: s.bearing = r.varint(); break;         // wire 0
            case 3: s.range = r.varint(); break;           // wire 0
            case 4: r.varint(); break;                     // time uint64 - skip; we don't use it
            case 5: s.data = r.bytes(); break;             // wire 2
            case 6: s.lat = r.double(); break;             // wire 1
            case 7: s.lon = r.double(); break;             // wire 1
            default: r.skip(wire); break;
        }
    }
}

// Reader walks a Uint8Array returning one wire-typed value at a
// time. Single-file, no class hierarchy: this is the only consumer.
class Reader {
    constructor(bytes) {
        this.reset(bytes);
    }

    /** Re-target the reader at a new buffer without allocating.
     *  The underlying DataView is rebuilt because it's anchored to
     *  a specific (buffer, byteOffset, byteLength) triplet; that's
     *  one allocation per decode rather than two. */
    reset(bytes) {
        this.buf = bytes;
        this.offset = 0;
        this.view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
        return this;
    }

    atEnd() { return this.offset >= this.buf.length; }

    /**
     * Reads a varint into a JS Number. Varints over 53 bits lose
     * precision; that's fine for every field we consume (uint32
     * values fit in 32 bits, the time uint64 is skipped).
     */
    varint() {
        let result = 0;
        let shift = 0;
        while (true) {
            if (this.offset >= this.buf.length) throw new Error('radar: varint ran off end');
            const b = this.buf[this.offset++];
            // For shift >= 28 we switch to multiplication to avoid
            // the <<32 overflow that turns a large uint32 negative.
            if (shift < 28) {
                result |= (b & 0x7f) << shift;
            } else {
                result += (b & 0x7f) * Math.pow(2, shift);
            }
            if ((b & 0x80) === 0) break;
            shift += 7;
            if (shift > 63) throw new Error('radar: varint > 10 bytes');
        }
        // <<32 overflow to negative via bitwise or above - fix up.
        return result < 0 ? result + 0x100000000 : result;
    }

    /** Reads a length-prefixed byte sequence as a view into the
     *  underlying buffer. No copy; the caller is expected to use it
     *  within the same tick as the decode. Bounds-checks BEFORE
     *  mutating offset so a malformed length (e.g. a varint claiming
     *  4 GB) doesn't leave the reader in an out-of-bounds state that
     *  any later code path would observe before the throw. */
    bytes() {
        const len = this.varint();
        const start = this.offset;
        const end = start + len;
        if (end > this.buf.length) throw new Error('radar: bytes len ran off end');
        this.offset = end;
        return new Uint8Array(this.buf.buffer, this.buf.byteOffset + start, len);
    }

    /** Reads an IEEE-754 little-endian double (wire type 1). Bounds-
     *  checks so a truncated frame throws our uniform "ran off end"
     *  error instead of DataView's own RangeError. */
    double() {
        if (this.offset + 8 > this.buf.length) throw new Error('radar: double ran off end');
        const d = this.view.getFloat64(this.offset, true);
        this.offset += 8;
        return d;
    }

    /** Skips a field of the given wire type. Bounds-checks so a
     *  truncated frame throws rather than silently running past the
     *  buffer. Wire types 3 / 4 (deprecated start/end group) are
     *  rejected - real proto3 servers never emit them. */
    skip(wire) {
        switch (wire) {
            case 0: this.varint(); break;            // varint
            case 1:
                if (this.offset + 8 > this.buf.length) throw new Error('radar: 64-fixed ran off end');
                this.offset += 8;
                break;
            case 2: {
                const len = this.varint();
                if (this.offset + len > this.buf.length) throw new Error('radar: skip bytes ran off end');
                this.offset += len;
                break;
            }
            case 5:
                if (this.offset + 4 > this.buf.length) throw new Error('radar: 32-fixed ran off end');
                this.offset += 4;
                break;
            default: throw new Error('radar: unknown wire type ' + wire);
        }
    }
}
