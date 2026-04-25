// Pure helpers for splitting an active route into "passed" and
// "future" segments based on the index of the next (un-reached)
// waypoint. Lives in its own module so the leafletInterop renderer
// stays a thin draw-call layer and the split logic can be exercised
// without a Leaflet/DOM stub.
//
// Convention used across the chartplotter:
//   - "passed"  = legs whose end-waypoint has already been reached.
//   - "future"  = legs that still lie ahead, including the leg the
//                 boat is currently on (prev WP -> next WP).
// The boundary point (last reached waypoint) is shared between the
// two segment lists so the rendered polylines join cleanly without a
// visual gap at the handover.

// Split a route into already-passed and still-planned coordinate
// runs based on the index of the next un-reached waypoint.
//
// Input:
//   coords = [[47.0, 8.0], [47.1, 8.1], [47.2, 8.2], [47.3, 8.3]]
//   wpIdx  = 2  (next WP is coords[2]; coords[0..1] reached)
// Output:
//   passed = [[47.0, 8.0], [47.1, 8.1]]            (1 leg already crossed)
//   future = [[47.1, 8.1], [47.2, 8.2], [47.3, 8.3]] (current + future legs)
//
// Edge cases:
//   - wpIdx <= 0: no legs passed, future is the whole route.
//   - wpIdx >= coords.length: every leg passed, future is empty.
//   - coords < 2 points: both arrays empty (caller should not draw).
export function splitRouteByProgress(coords, wpIdx) {
    if (!Array.isArray(coords) || coords.length < 2) {
        return { passed: [], future: [] };
    }
    // Clamp into [0, coords.length] so out-of-range wpIdx degrades
    // gracefully. The C# caller passes the closest-match index from a
    // float lat/lon; an out-of-route boat position should not crash
    // the renderer, just produce an empty passed list.
    const idx = Math.max(0, Math.min(coords.length, wpIdx | 0));

    // Passed segment includes points 0..idx-1 (waypoints already
    // reached). Needs at least 2 points to draw, otherwise the
    // caller skips it.
    const passed = idx >= 2 ? coords.slice(0, idx) : [];

    // Future segment starts at the last reached waypoint (idx-1) so
    // the current leg is included in the planned/solid line. When
    // idx == 0 (no waypoint reached yet) we start from the route
    // origin instead.
    const futureStart = idx > 0 ? idx - 1 : 0;
    const future = futureStart < coords.length - 1 ? coords.slice(futureStart) : [];

    return { passed, future };
}
