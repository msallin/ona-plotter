// Tests for courseLineLayer.js diff behaviour.
// Run with: node --test OnaPlotter/wwwroot/js/courseLineLayer.test.js
//
// Pins the contract that setCourseLine only calls the underlying
// Leaflet primitives when the inputs they depend on actually change:
//   * bearing line tracks both endpoints (boat + WP)
//   * pulse marker tracks the WP only
//   * arrival ring tracks WP + radius
// Without these guards, every SK self-delta (~1-10 Hz on a busy
// feed) repaints Leaflet's marker + circle even when the helm is
// idle in port and nothing on the course actually moved.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';

// Build a fresh Leaflet stub + module instance per test. Importing
// courseLineLayer.js requires globalThis.L to be defined because the
// module evaluates `L.divIcon(...)` at top level for the pulse icon;
// each test sets up its own stub via a cache-busted dynamic import
// so the per-test instance starts clean.
async function freshLayer() {
    const calls = {
        polyline: 0, marker: 0, circle: 0,
        bearingSetLatLngs: 0,
        pulseSetLatLng: 0,
        ringSetLatLng: 0, ringSetRadius: 0,
        removeLayer: 0,
    };

    const fakePolyline = {
        setLatLngs: () => { calls.bearingSetLatLngs++; },
        setStyle: () => {},
        addTo: () => fakePolyline,
    };
    const fakeMarker = {
        setLatLng: () => { calls.pulseSetLatLng++; },
        setOpacity: () => {},
        addTo: () => fakeMarker,
    };
    const fakeCircle = {
        setLatLng: () => { calls.ringSetLatLng++; },
        setRadius: () => { calls.ringSetRadius++; },
        setStyle: () => {},
        addTo: () => fakeCircle,
    };
    const fakeMap = { removeLayer: () => { calls.removeLayer++; } };

    globalThis.L = {
        polyline: () => { calls.polyline++; return fakePolyline; },
        marker:   () => { calls.marker++;   return fakeMarker;   },
        circle:   () => { calls.circle++;   return fakeCircle;   },
        divIcon:  () => ({}),
    };

    // Cache-bust so each test gets a module instance with fresh
    // module-scope state (the diff caches live there).
    const mod = await import(`./courseLineLayer.js?t=${Date.now()}_${Math.random()}`);
    mod.init(fakeMap, { colors: { bearing: '#000' } });
    return { mod, calls };
}

describe('setCourseLine diff guards', () => {
    it('first call creates all three layers', async () => {
        const { mod, calls } = await freshLayer();
        mod.setCourseLine(54.0, 11.0, 54.5, 11.5, 100);

        assert.equal(calls.polyline, 1, 'bearing polyline created');
        assert.equal(calls.marker,   1, 'pulse marker created');
        assert.equal(calls.circle,   1, 'arrival ring created');
        // create-path doesn't go through setters; those caches seeded only.
        assert.equal(calls.bearingSetLatLngs, 0);
        assert.equal(calls.pulseSetLatLng,   0);
        assert.equal(calls.ringSetLatLng,    0);
        assert.equal(calls.ringSetRadius,    0);
    });

    it('identical second call skips every redraw', async () => {
        // The steady-state idle case: same SK feed pushing position
        // deltas while anchored, no WP change, no radius change.
        const { mod, calls } = await freshLayer();
        mod.setCourseLine(54.0, 11.0, 54.5, 11.5, 100);
        mod.setCourseLine(54.0, 11.0, 54.5, 11.5, 100);

        assert.equal(calls.bearingSetLatLngs, 0, 'bearing skipped');
        assert.equal(calls.pulseSetLatLng,   0, 'pulse skipped');
        assert.equal(calls.ringSetLatLng,    0, 'ring position skipped');
        assert.equal(calls.ringSetRadius,    0, 'ring radius skipped');
    });

    it('boat-only movement repaints the bearing line, leaves WP markers alone', async () => {
        // Boat is moving, WP stationary - bearing endpoint changes
        // but the pulse + arrival ring should stay put. This is the
        // common underway case; without the guard the ring's
        // metres->pixels reprojection runs per delta for no visible
        // change.
        const { mod, calls } = await freshLayer();
        mod.setCourseLine(54.0, 11.0, 54.5, 11.5, 100);
        mod.setCourseLine(54.01, 11.01, 54.5, 11.5, 100);

        assert.equal(calls.bearingSetLatLngs, 1, 'bearing repainted');
        assert.equal(calls.pulseSetLatLng,   0, 'pulse skipped');
        assert.equal(calls.ringSetLatLng,    0, 'ring position skipped');
        assert.equal(calls.ringSetRadius,    0, 'ring radius skipped');
    });

    it('leg advance (new WP) repaints bearing + pulse + ring', async () => {
        const { mod, calls } = await freshLayer();
        mod.setCourseLine(54.0, 11.0, 54.5, 11.5, 100);
        mod.setCourseLine(54.0, 11.0, 55.0, 12.0, 100);

        assert.equal(calls.bearingSetLatLngs, 1, 'bearing repainted (wp changed)');
        assert.equal(calls.pulseSetLatLng,   1, 'pulse repainted');
        assert.equal(calls.ringSetLatLng,    1, 'ring repositioned');
        assert.equal(calls.ringSetRadius,    1, 'ring radius re-applied (setLatLng + setRadius are paired)');
    });

    it('arrival-radius change repaints only the ring', async () => {
        // Helm tweaks the arrival radius from the Settings page - the
        // ring needs to redraw but the bearing line + pulse are anchored
        // at unchanged coords.
        const { mod, calls } = await freshLayer();
        mod.setCourseLine(54.0, 11.0, 54.5, 11.5, 100);
        mod.setCourseLine(54.0, 11.0, 54.5, 11.5, 200);

        assert.equal(calls.bearingSetLatLngs, 0, 'bearing skipped');
        assert.equal(calls.pulseSetLatLng,   0, 'pulse skipped');
        assert.equal(calls.ringSetLatLng,    1, 'ring repositioned');
        assert.equal(calls.ringSetRadius,    1, 'ring radius re-applied');
    });

    it('clearCourseLine resets caches so next setCourseLine re-creates layers', async () => {
        // After teardown, a subsequent setCourseLine call must create
        // fresh layers rather than calling setLatLng on a removed
        // (and therefore stale-cached) reference.
        const { mod, calls } = await freshLayer();
        mod.setCourseLine(54.0, 11.0, 54.5, 11.5, 100);
        mod.clearCourseLine();
        mod.setCourseLine(54.0, 11.0, 54.5, 11.5, 100);

        assert.equal(calls.polyline, 2, 'bearing polyline created twice');
        assert.equal(calls.marker,   2, 'pulse marker created twice');
        assert.equal(calls.circle,   2, 'arrival ring created twice');
        assert.equal(calls.removeLayer, 3, 'all three layers removed on clear');
    });

    it('dispose then init re-creates layers on the next setCourseLine', async () => {
        // Production singleton lifecycle: Map page unmount calls
        // dispose(), a subsequent re-mount calls init() then
        // setCourseLine() if a course is still active. The diff caches
        // MUST be reset by dispose() or the new layers would never be
        // created (the cache would still match the inputs and the
        // create branch's `!layer` check would be true but the
        // identity-compare cache would suppress the updates if dispose
        // had not reset them - here verifying creation itself).
        const { mod, calls } = await freshLayer();
        const fakeMap = { removeLayer: () => { calls.removeLayer++; } };

        mod.setCourseLine(54.0, 11.0, 54.5, 11.5, 100);
        mod.dispose();
        mod.init(fakeMap, { colors: { bearing: '#000' } });
        mod.setCourseLine(54.0, 11.0, 54.5, 11.5, 100);

        assert.equal(calls.polyline, 2, 'bearing polyline created twice');
        assert.equal(calls.marker,   2, 'pulse marker created twice');
        assert.equal(calls.circle,   2, 'arrival ring created twice');
    });

    it('setCoursePulseSuppressed(false) after suppress + setCourseLine re-creates the pulse', async () => {
        // When the active-route layer takes over the WP marker
        // (setCoursePulseSuppressed(true)), this layer tears down its
        // own pulse + nulls the pulse cache. A subsequent unsuppress
        // followed by setCourseLine with identical coords MUST create
        // a fresh pulse marker rather than skip on a now-stale
        // identical-coord cache.
        const { mod, calls } = await freshLayer();
        mod.setCourseLine(54.0, 11.0, 54.5, 11.5, 100);
        mod.setCoursePulseSuppressed(true);
        mod.setCoursePulseSuppressed(false);
        mod.setCourseLine(54.0, 11.0, 54.5, 11.5, 100);

        assert.equal(calls.marker, 2, 'pulse marker created twice');
        assert.equal(calls.pulseSetLatLng, 0, 'no setLatLng on re-create');
    });

    it('arrival ring radius=0 teardown then radius>0 re-creates the ring', async () => {
        // Helm disables the APPROACH alarm (radius -> 0), then re-
        // enables it. The teardown branch must reset the ring cache so
        // the re-create path actually fires; otherwise a stale cache
        // could leave the ring missing while the inputs look unchanged
        // to the diff.
        const { mod, calls } = await freshLayer();
        mod.setCourseLine(54.0, 11.0, 54.5, 11.5, 100);
        mod.setCourseLine(54.0, 11.0, 54.5, 11.5, 0);    // teardown
        mod.setCourseLine(54.0, 11.0, 54.5, 11.5, 100);  // re-create

        assert.equal(calls.circle, 2, 'arrival ring created twice');
        assert.equal(calls.removeLayer, 1, 'ring removed during teardown');
    });
});
