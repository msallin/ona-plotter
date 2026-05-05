// Pure geometry and navigation math utilities.
// Extracted from leafletInterop.js so they can be tested without a browser/Leaflet.
//
// Display formatters (speed colour, lat/lon DMS, knots conversion,
// range-ring label, ETA, MOB elapsed, AIS staleness opacity) live in
// `format.js` -- their canonical home is C# Utilities/Format.cs +
// SpeedColor.cs + StalenessOpacity.cs and the JS module mirrors those
// with cross-reference comments. Re-exported below for back-compat
// with existing callers; new code should import from `format.js`.

import { speedColor, speedBucket, SPEED_BUCKETS, MS_TO_KNOTS } from './format.js';
export { speedColor, speedBucket, SPEED_BUCKETS, MS_TO_KNOTS };

export const RAD = Math.PI / 180;
export const DEG = 180 / Math.PI;
export const NM_PER_METER = 1 / 1852;
// Default look-ahead window for COG vectors. Both own-vessel and AIS
// targets used to share this constant. C# now exposes two helm-tunable
// settings (OwnCogVectorMinutes / AisCogVectorMinutes); the JS layer
// reads those from per-call parameters with this default as a fallback
// when a caller doesn't pass an explicit minutes value.
export const VECTOR_MINUTES = 10;

/** Haversine distance in meters between two lat/lon points (degrees). */
export function haversineMeters(lat1, lon1, lat2, lon2) {
    const R = 6371000;
    const dLat = (lat2 - lat1) * RAD;
    const dLon = (lon2 - lon1) * RAD;
    const a = Math.sin(dLat/2)**2 + Math.cos(lat1*RAD) * Math.cos(lat2*RAD) * Math.sin(dLon/2)**2;
    return R * 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1-a));
}

/** Initial bearing in degrees (0-360) from point 1 to point 2 (lat/lon in degrees). */
export function bearingDeg(lat1, lon1, lat2, lon2) {
    const dLon = (lon2 - lon1) * RAD;
    const y = Math.sin(dLon) * Math.cos(lat2 * RAD);
    const x = Math.cos(lat1 * RAD) * Math.sin(lat2 * RAD) -
              Math.sin(lat1 * RAD) * Math.cos(lat2 * RAD) * Math.cos(dLon);
    return ((Math.atan2(y, x) * DEG) + 360) % 360;
}

/** Destination point given start (degrees), bearing (radians), distance (meters). */
export function destPoint(lat, lon, bearingRad, distM) {
    const R = 6371000;
    const lr = lat * RAD, lnr = lon * RAD;
    const lat2 = Math.asin(Math.sin(lr)*Math.cos(distM/R) + Math.cos(lr)*Math.sin(distM/R)*Math.cos(bearingRad));
    const lon2 = lnr + Math.atan2(Math.sin(bearingRad)*Math.sin(distM/R)*Math.cos(lr),
                                   Math.cos(distM/R) - Math.sin(lr)*Math.sin(lat2));
    return [lat2*DEG, lon2*DEG];
}

/**
 * Course vector endpoint: returns [lat, lon] or null if speed too low.
 * `minutes` defaults to VECTOR_MINUTES when omitted; pass an explicit
 * value to use a per-vessel-class look-ahead (own vs AIS).
 */
export function vectorEnd(lat, lon, cogRad, sogMs, minutes) {
    if (cogRad == null || sogMs == null || sogMs < 0.1) return null;
    const m = (typeof minutes === 'number' && isFinite(minutes) && minutes > 0)
        ? minutes : VECTOR_MINUTES;
    return destPoint(lat, lon, cogRad, sogMs * m * 60);
}

// speedColor / speedBucket / SPEED_BUCKETS / MS_TO_KNOTS are
// re-exported from `format.js` at the top of this file.
