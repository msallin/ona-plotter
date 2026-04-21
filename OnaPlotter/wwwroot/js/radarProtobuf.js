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

/**
 * Decodes a RadarMessage from a Uint8Array. Unknown fields are
 * skipped silently (forward-compat with server versions that add
 * fields we don't know about).
 *
 * @param {Uint8Array} bytes
 * @returns {{ spokes: Array<Spoke> }}
 */
export function decodeRadarMessage(bytes) {
    const r = new Reader(bytes);
    const spokes = [];
    while (!r.atEnd()) {
        const tag = r.varint();
        const field = tag >>> 3;
        const wire = tag & 7;
        if (field === 2 && wire === 2) {
            // Repeated Spoke: length-prefixed embedded message.
            const len = r.varint();
            const end = r.offset + len;
            spokes.push(decodeSpoke(r, end));
        } else {
            r.skip(wire);
        }
    }
    return { spokes };
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

/** @returns {Spoke} */
function decodeSpoke(r, end) {
    // Default fields: angle and range are required per schema; the
    // "optional" kind is represented by absence below. data is always
    // present but may be zero-length.
    let angle = 0, range = 0;
    let bearing, lat, lon;
    /** @type {Uint8Array} */
    let data = EMPTY_BYTES;
    while (r.offset < end) {
        const tag = r.varint();
        const field = tag >>> 3;
        const wire = tag & 7;
        switch (field) {
            case 1: angle = r.varint(); break;           // wire 0
            case 2: bearing = r.varint(); break;         // wire 0
            case 3: range = r.varint(); break;           // wire 0
            case 4: r.varint(); break;                   // time uint64 -- skip; we don't use it
            case 5: data = r.bytes(); break;             // wire 2
            case 6: lat = r.double(); break;             // wire 1
            case 7: lon = r.double(); break;             // wire 1
            default: r.skip(wire); break;
        }
    }
    // Object literal is hot-path; avoid conditional property creation
    // because V8 de-optimises polymorphic shapes. Undefined fields are
    // cheap to read as undefined.
    return { angle, bearing, range, data, lat, lon };
}

const EMPTY_BYTES = new Uint8Array(0);

// Reader walks a Uint8Array returning one wire-typed value at a
// time. Single-file, no class hierarchy: this is the only consumer.
class Reader {
    constructor(bytes) {
        this.buf = bytes;
        this.offset = 0;
        // Shared DataView for double reads. Created once -- constructing
        // one per spoke cost ~15% of total decode time in profiling.
        this.view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
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
        // <<32 overflow to negative via bitwise or above -- fix up.
        return result < 0 ? result + 0x100000000 : result;
    }

    /** Reads a length-prefixed byte sequence as a view into the
     *  underlying buffer. No copy; the caller is expected to use it
     *  within the same tick as the decode. */
    bytes() {
        const len = this.varint();
        const start = this.offset;
        this.offset += len;
        if (this.offset > this.buf.length) throw new Error('radar: bytes len ran off end');
        return new Uint8Array(this.buf.buffer, this.buf.byteOffset + start, len);
    }

    /** Reads an IEEE-754 little-endian double (wire type 1). */
    double() {
        const d = this.view.getFloat64(this.offset, true);
        this.offset += 8;
        return d;
    }

    /** Skips a field of the given wire type. Unknown-field tolerance. */
    skip(wire) {
        switch (wire) {
            case 0: this.varint(); break;            // varint
            case 1: this.offset += 8; break;         // 64-bit fixed
            case 2: { const len = this.varint(); this.offset += len; break; }
            case 5: this.offset += 4; break;         // 32-bit fixed
            default: throw new Error('radar: unknown wire type ' + wire);
        }
    }
}
