// Pure geometry and navigation math utilities.
// Extracted from leafletInterop.js so they can be tested without a browser/Leaflet.

export const RAD = Math.PI / 180;
export const DEG = 180 / Math.PI;
export const NM_PER_METER = 1 / 1852;
export const VECTOR_MINUTES = 5;

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

/** Course vector endpoint: returns [lat, lon] or null if speed too low. */
export function vectorEnd(lat, lon, cogRad, sogMs) {
    if (cogRad == null || sogMs == null || sogMs < 0.1) return null;
    return destPoint(lat, lon, cogRad, sogMs * VECTOR_MINUTES * 60);
}

/**
 * Closest Point of Approach calculation.
 * Returns { cpa (nm), tcpa (minutes) } or null.
 */
export function computeCpa(lat1, lon1, cog1, sog1, lat2, lon2, cog2, sog2) {
    if (cog1 == null || sog1 == null || cog2 == null || sog2 == null) return null;
    if (sog1 < 0.1 && sog2 < 0.1) return null;

    const midLat = (lat1 + lat2) / 2;
    const cosLat = Math.cos(midLat * RAD);
    const mPerDegLat = 111320;
    const mPerDegLon = 111320 * cosLat;

    const x1 = 0, y1 = 0;
    const x2 = (lon2 - lon1) * mPerDegLon;
    const y2 = (lat2 - lat1) * mPerDegLat;

    const vx1 = Math.sin(cog1) * sog1, vy1 = Math.cos(cog1) * sog1;
    const vx2 = Math.sin(cog2) * sog2, vy2 = Math.cos(cog2) * sog2;

    const dvx = vx1 - vx2, dvy = vy1 - vy2;
    const dpx = x1 - x2, dpy = y1 - y2;

    const a = dvx*dvx + dvy*dvy;
    if (a < 0.001) {
        const dist = Math.sqrt(dpx*dpx + dpy*dpy);
        return { cpa: dist * NM_PER_METER, tcpa: 0 };
    }

    const t = -(dpx*dvx + dpy*dvy) / a;
    if (t < 0) return null;

    const cx = dpx + dvx*t, cy = dpy + dvy*t;
    const cpaDist = Math.sqrt(cx*cx + cy*cy);

    return { cpa: cpaDist * NM_PER_METER, tcpa: t / 60 };
}

/** Speed-to-color mapping: sogMs -> CSS rgb string. Blue(0) -> Green(3kn) -> Yellow(6+kn). */
export function speedColor(sogMs) {
    if (sogMs == null) return '#3b82f6';
    const kn = sogMs * 1.94384;
    const t = Math.min(kn / 8, 1);
    if (t < 0.5) {
        const f = t * 2;
        const r = Math.round(59 + f * (34 - 59));
        const g = Math.round(130 + f * (197 - 130));
        const b = Math.round(246 + f * (94 - 246));
        return `rgb(${r},${g},${b})`;
    } else {
        const f = (t - 0.5) * 2;
        const r = Math.round(34 + f * (234 - 34));
        const g = Math.round(197 + f * (179 - 197));
        const b = Math.round(94 + f * (8 - 94));
        return `rgb(${r},${g},${b})`;
    }
}

/** Speed bucket thresholds (m/s) for track segment grouping. */
export const SPEED_BUCKETS = [0, 1, 2, 3, 5, 8];
export function speedBucket(sogMs) {
    if (sogMs == null) return 0;
    for (let i = SPEED_BUCKETS.length - 1; i >= 0; i--) {
        if (sogMs >= SPEED_BUCKETS[i]) return i;
    }
    return 0;
}
