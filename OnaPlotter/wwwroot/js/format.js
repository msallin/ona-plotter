// Single source of truth for the JS-side renderer formatters.
//
// Every helper here mirrors a tested C# canonical in
// OnaPlotter/Utilities/ - the C# tests pin the exact strings, this
// module reproduces the formula so JS-tick-rate callers don't need
// an interop round-trip per render. If you change one side, change
// the other - the C# tests are the contract; this file is the
// renderer-side mirror.
//
// Cross-references (each function names its C# canonical):
//   - msToKnots      -> Format.MsToKnots                (Format.cs)
//   - speedColor     -> SpeedColor.Rgb                  (SpeedColor.cs)
//   - speedBucket    -> SpeedColor.Bucket               (SpeedColor.cs)
//   - stalenessOp    -> StalenessOpacity.Compute        (StalenessOpacity.cs)
//   - mobElapsed     -> Format.MobElapsed               (Format.cs)
//   - latLonDms      -> Format.LatLonDms                (Format.cs)
//   - rangeRingLabel -> Format.RangeRingLabel           (Format.cs)
//   - etaWithTtg     -> Format.EtaWithTtg               (Format.cs)

// --- Constants (mirror Format.cs) -------------------------------

/** m/s -> knots multiplier. Matches Format.MsToKnots. */
export const MS_TO_KNOTS = 1.94384;

/** SpeedColor.DefaultRgb - default colour when SOG is unknown. */
export const SPEED_COLOR_DEFAULT = '#3b82f6';

/** SpeedColor.Buckets - per-bucket lower bound (m/s). */
export const SPEED_BUCKETS = [0, 1, 2, 3, 5, 8];

/** StalenessOpacity threshold constants. */
export const STALE_FRESH_SECONDS = 30;
export const STALE_FADED_SECONDS = 300;
export const STALE_FLOOR_OPACITY = 0.25;
export const STALE_FADE_SPAN = 0.65;

// --- Helpers ----------------------------------------------------

/**
 * SpeedColor.Rgb mirror. Returns "rgb(r,g,b)" for the given SOG
 * (m/s), or SPEED_COLOR_DEFAULT when sog is null/undefined.
 * Two-stage lerp: 0..3 kn blue->green; 3..8 kn green->yellow.
 */
export function speedColor(sogMs) {
    if (sogMs == null) return SPEED_COLOR_DEFAULT;
    const kn = sogMs * MS_TO_KNOTS;
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

/** SpeedColor.Bucket mirror. Returns the bucket index (0..5). */
export function speedBucket(sogMs) {
    if (sogMs == null) return 0;
    for (let i = SPEED_BUCKETS.length - 1; i >= 0; i--) {
        if (sogMs >= SPEED_BUCKETS[i]) return i;
    }
    return 0;
}

/**
 * StalenessOpacity.Compute mirror. Returns the inline-style opacity
 * string for an AIS marker of the given age (seconds), or null when
 * the marker is fresh (caller clears any previous inline opacity so
 * CSS defaults apply).
 */
export function stalenessOpacity(ageSec) {
    if (ageSec < STALE_FRESH_SECONDS) return null;
    if (ageSec >= STALE_FADED_SECONDS) {
        return STALE_FLOOR_OPACITY.toFixed(2);
    }
    const op = 1 - STALE_FADE_SPAN * (ageSec - STALE_FRESH_SECONDS) / (STALE_FADED_SECONDS - STALE_FRESH_SECONDS);
    return op.toFixed(2);
}

/**
 * Format.MobElapsed mirror. Returns "T+5s" / "T+12m" / "T+1h23m"
 * for elapsed seconds since the MOB raise. Negative input clamps
 * to "T+0s".
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
 * Format.LatLonDms mirror. Returns "47.50000°N 8.50000°W"
 * (or with a custom separator e.g. ", ") with five decimals
 * (~1m precision). Default separator is a single space, matching
 * the C# canonical - callers that need a comma pass
 * <c>latLonDms(lat, lon, ', ')</c>.
 */
export function latLonDms(lat, lon, sep = ' ') {
    return `${latDms(lat)}${sep}${lonDms(lon)}`;
}

/**
 * Format.RangeRingLabel mirror. Returns "0.5 nm" / "1.5 nm" /
 * "5 nm" / "" for non-finite or zero-or-negative input. Trailing
 * zeros are stripped ("0.50" -> "0.5", "1.0" -> "1") to keep
 * chart labels compact - both AIS guard rings and radar range
 * rings use this formatter.
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
 * RouteEta.Format mirror - canonical: OnaPlotter/Utilities/RouteEta.cs.
 * Returns the popup ETA line "ETA HH:MM (in 1h 23m)" / "ETA HH:MM
 * (in 5m)" / "ETA HH:MM (in >99h)", or null on null/non-finite/non-
 * positive input (callers drop the row in those cases). Hour cap
 * (99h), minute round, 1m floor all match the C# port; the parity
 * test scrapes this file for the matching tokens (see
 * RouteEtaJsParityTests).
 */
export function etaWithTtg(ttgSeconds) {
    if (ttgSeconds == null || !isFinite(ttgSeconds) || ttgSeconds <= 0) return null;
    const totalMin = Math.max(1, Math.round(ttgSeconds / 60));
    const totalH = Math.floor(totalMin / 60);
    let inText;
    if (totalH > 99) {
        inText = '>99h';
    } else if (totalH > 0) {
        inText = `${totalH}h ${totalMin % 60}m`;
    } else {
        inText = `${totalMin}m`;
    }
    const eta = new Date(Date.now() + ttgSeconds * 1000);
    const pad2 = (n) => String(n).padStart(2, '0');
    const clock = `${pad2(eta.getHours())}:${pad2(eta.getMinutes())}`;
    return `ETA ${clock} (in ${inText})`;
}
