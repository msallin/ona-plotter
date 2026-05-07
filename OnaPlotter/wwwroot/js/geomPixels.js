// Pure 2D Euclidean-pixel helpers used by route-edit + measure
// modules to find "the closest segment to this click" for vertex
// insertion. Extracted from leafletInterop.js so the math is
// importable + testable without standing up a Leaflet map.
//
// All inputs / outputs are pixel-space points expressed as
// {x, y} pairs (the shape Leaflet's latLngToLayerPoint emits).
// No lat/lon / spherical math here - the caller is expected to
// project before invoking.

/**
 * Euclidean pixel distance from point p to the segment a-b.
 *
 * Returns the perpendicular-distance for points whose foot lies
 * inside the segment, or the endpoint distance for points whose
 * foot lies past either end. Degenerate zero-length segments
 * (a coincides with b) return the distance from p to the
 * coincident point so the caller doesn't have to special-case it.
 *
 * @param {{x:number,y:number}} p  Probe point.
 * @param {{x:number,y:number}} a  Segment start.
 * @param {{x:number,y:number}} b  Segment end.
 * @returns {number} Distance in pixels.
 */
export function pointToSegmentPixels(p, a, b) {
    const dx = b.x - a.x, dy = b.y - a.y;
    const len2 = dx * dx + dy * dy;
    if (len2 === 0) return Math.hypot(p.x - a.x, p.y - a.y);
    let t = ((p.x - a.x) * dx + (p.y - a.y) * dy) / len2;
    t = Math.max(0, Math.min(1, t));
    const cx = a.x + t * dx, cy = a.y + t * dy;
    return Math.hypot(p.x - cx, p.y - cy);
}
