// Render-time formatters for the JS map layers.
//
// Everything here runs where an interop round-trip per frame is not an
// option: ticking MOB labels, measure-tool drags, radar range rings
// whose range arrives in the spoke stream, popups re-rendered after a
// marker drag. Values that C# already knows (AIS opacity, colours,
// titles, distances, track colours) are stamped on the payloads
// instead; nothing in this module mirrors a C# decision.
// format.test.js pins the outputs.

/** m/s -> knots multiplier. */
export const MS_TO_KNOTS = 1.94384;

/** Meters per nautical mile. */
export const METERS_PER_NM = 1852;

/**
 * MOB elapsed label: "T+5s" / "T+12m" / "T+1h23m" for elapsed
 * seconds since the MOB raise. Negative input (clock skew) clamps to
 * "T+0s". Re-rendered every second by the MOB pin label.
 */
export function mobElapsed(seconds) {
    let sec = Math.floor(seconds);
    if (sec < 0) sec = 0;
    if (sec < 60) return `T+${sec}s`;
    const min = Math.floor(sec / 60);
    if (min < 60) return `T+${min}m`;
    const hr = Math.floor(min / 60);
    return `T+${hr}h${min % 60}m`;
}

/** Single-axis lat formatter - "47.50000°N". Used by popup
 * tables that put lat / lon in separate cells. */
export function latDms(lat) {
    const ns = lat >= 0 ? 'N' : 'S';
    return `${Math.abs(lat).toFixed(5)}°${ns}`;
}

/** Single-axis lon formatter - "8.50000°E". */
export function lonDms(lon) {
    const ew = lon >= 0 ? 'E' : 'W';
    return `${Math.abs(lon).toFixed(5)}°${ew}`;
}

/**
 * Lat/lon label: "47.50000°N 8.50000°W" (or with a custom separator
 * e.g. ", ") with five decimals (~1 m precision). Popups re-render it
 * after a marker drag. Same output as C# Format.LatLonDms, pinned by
 * the shared vectors in format.test.js / FormatTests.
 */
export function latLonDms(lat, lon, sep = ' ') {
    return `${latDms(lat)}${sep}${lonDms(lon)}`;
}

/**
 * Range-ring label: "0.5 nm" / "1.5 nm" / "5 nm", or "" for
 * non-finite or zero-or-negative input. Trailing zeros are stripped
 * ("0.50" -> "0.5", "1.0" -> "1") to keep chart labels compact. Used
 * by the distance rings and the radar range rings, whose range
 * arrives in the radar spoke stream.
 */
export function rangeRingLabel(nm) {
    if (!isFinite(nm) || nm <= 0) return '';
    let digits;
    if (nm < 1) {
        digits = nm.toFixed(2).replace(/\.?0+$/, '');
    } else if (nm < 10) {
        digits = nm.toFixed(1).replace(/\.0$/, '');
    } else {
        digits = Math.round(nm).toString();
    }
    return `${digits} nm`;
}

/**
 * Measure-tool segment label: "0.85 nm / 1574 m". Two decimals on
 * nm; metres rounded to the nearest whole. Re-rendered on every drag
 * frame of a measure vertex.
 */
export function measureDistance(meters) {
    const nm = meters / METERS_PER_NM;
    const mRounded = Math.round(meters);
    return `${nm.toFixed(2)} nm / ${mRounded} m`;
}
