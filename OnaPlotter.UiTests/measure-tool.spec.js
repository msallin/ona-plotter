// Measure-tool guard.
//
// Pins the behaviour added under the "Vessel -> point measurement" item
// in Plotter TODO: the Measure tool now supports a vessel-anchored
// point that tracks own-boat as it moves, and the
// "Measure Here" entry in the long-press / right-click context menu
// drops a fresh two-point measurement (vessel + clicked spot) without
// the helm needing to know the old undocumented double-click gesture.
//
// Tests at the JS-interop layer (the same dynamic-import pattern as
// osm-attribution.spec.js) so we exercise the actual measureFromVesselTo
// / setMeasureMode flow Blazor invokes, not a synthetic Leaflet stub.
//
// Run with:
//   BASE_URL=http://localhost:5282 \
//     npx playwright test measure-tool.spec.js

import { test, expect } from '@playwright/test';

test.describe('Measure tool', () => {
    test.beforeEach(async ({ page }) => {
        // Pre-dismiss first-run modals so the welcome card doesn't sit
        // over the map and slow the test.
        await page.addInitScript(() => {
            localStorage.setItem('ona.hints.welcome.v1.dismissed', '1');
            localStorage.setItem('ona.hints.mapLongPress.dismissed', '1');
        });
        // No SignalK server in this spec; route the WS so the Blazor
        // app doesn't enter its reconnect loop and stays render-idle.
        await page.routeWebSocket(/.*\/signalk\/v1\/stream.*/, ws => {
            ws.onMessage(() => {});
        });
        // Stub the SK HTTP API endpoints so the toast-error stack
        // doesn't pile up at the bottom of the page.
        await page.route(/\/signalk\/v\d+\/api\/(resources|vessels)\/.*/, route => {
            route.fulfill({ status: 200, contentType: 'application/json', body: '{}' });
        });
        await page.goto('/map');
        await page.waitForSelector('.leaflet-container', { timeout: 15000 });
    });

    test('measureFromVesselTo drops two points and activates measure mode', async ({ page }) => {
        const counts = await page.evaluate(async () => {
            const mod = await import('/_content/OnaPlotter/js/leafletInterop.js')
                .catch(() => import('/js/leafletInterop.js'));
            // Seed an own-boat position so the vessel-anchored point has
            // real coords. updatePosition stashes selfLat/selfLon used by
            // measureFromVesselTo.
            mod.updatePosition(48.0, 7.85, 0, 0, 0);
            mod.measureFromVesselTo(48.01, 7.86);
            return {
                dots: document.querySelectorAll('.ona-measure-dot').length,
                lines: document.querySelectorAll('.ona-measure-line').length,
            };
        });
        // Two circle markers (vessel + clicked) and one segment polyline.
        expect(counts.dots).toBe(2);
        expect(counts.lines).toBe(1);
        // The map cursor should be crosshair (measure-mode visual hint).
        const cursor = await page.evaluate(() => {
            return getComputedStyle(document.querySelector('.leaflet-container')).cursor;
        });
        expect(cursor).toBe('crosshair');
    });

    test('measure points are white, not cyan/amber', async ({ page }) => {
        const result = await page.evaluate(async () => {
            const mod = await import('/_content/OnaPlotter/js/leafletInterop.js')
                .catch(() => import('/js/leafletInterop.js'));
            mod.updatePosition(48.0, 7.85, 0, 0, 0);
            mod.measureFromVesselTo(48.01, 7.86);
            // The fixed measure dot is rendered as a DIV via L.divIcon
            // (so it can be draggable); read its CSS background.
            const fixedDot = document.querySelector('.ona-measure-dot:not(.ona-measure-dot-vessel)');
            const vesselDot = document.querySelector('.ona-measure-dot-vessel');
            const line = document.querySelector('.ona-measure-line');
            return {
                fixedBg: fixedDot ? getComputedStyle(fixedDot).backgroundColor : null,
                vesselBorder: vesselDot ? getComputedStyle(vesselDot).borderColor : null,
                lineStroke: line ? line.getAttribute('stroke') : null,
            };
        });
        // The unified colour is #e2e8f0 (slate-200, the same hue the old
        // vessel-to-point bearing line used). The browser normalises
        // hex to rgb() in computed style, so check both the fixed dot's
        // background, the vessel-anchored dot's border, and the SVG
        // segment line's stroke (which keeps the literal hex).
        expect(result.fixedBg).toBe('rgb(226, 232, 240)');
        expect(result.vesselBorder).toBe('rgb(226, 232, 240)');
        expect((result.lineStroke || '').toLowerCase()).toBe('#e2e8f0');
    });

    test('vessel-anchored segment redraws when own-boat moves', async ({ page }) => {
        const result = await page.evaluate(async () => {
            const mod = await import('/_content/OnaPlotter/js/leafletInterop.js')
                .catch(() => import('/js/leafletInterop.js'));
            // Snap the map onto the test region with fitBounds so the
            // measurement points project to distinct pixels regardless
            // of the page's default zoom. fitBounds is synchronous on
            // the Leaflet map instance (panTo + zoomIn animate, which
            // the Blazor server-rendered page doesn't get a chance to
            // settle before we read the SVG path back).
            mod.fitBounds(47.5, 7.5, 49.5, 8.5);
            mod.updatePosition(48.0, 7.85, 0, 0, 0);
            mod.measureFromVesselTo(49.0, 7.85);
            const before = document
                .querySelector('.ona-measure-line')
                ?.getAttribute('d');
            // Move the vessel ~30 nm north so the redraw produces a
            // visibly different polyline.
            mod.updatePosition(48.5, 7.85, 0, 0, 0);
            const after = document
                .querySelector('.ona-measure-line')
                ?.getAttribute('d');
            return { before, after };
        });
        // Both should be valid SVG path strings with a moveto + lineto.
        // Leaflet produces paths like "M605 24L605 24" (no space before
        // the L) so we just assert the two commands are present rather
        // than pinning a specific separator.
        expect(result.before).toMatch(/^M[^L]*L/);
        expect(result.after).toMatch(/^M[^L]*L/);
        // And the redraw must have produced a different geometry.
        expect(result.after).not.toBe(result.before);
    });

    test('right-click in measure mode clears the ruler without exiting', async ({ page }) => {
        // The reset gesture: while in measure mode, a right-click /
        // long-press wipes the current measurement and stays in measure
        // mode (cursor stays as crosshair). Find the map in the
        // Leaflet-managed Maps registry rather than wiring a test-only
        // export -- L.DomUtil keeps every map reachable via its DOM
        // container and that's stable across versions.
        const result = await page.evaluate(async () => {
            const mod = await import('/_content/OnaPlotter/js/leafletInterop.js')
                .catch(() => import('/js/leafletInterop.js'));
            mod.updatePosition(48.0, 7.85, 0, 0, 0);
            mod.measureFromVesselTo(48.01, 7.86);
            const dotsBefore = document.querySelectorAll('.ona-measure-dot').length;
            // Leaflet stores the map instance on the container as
            // _leaflet_id; the Map object itself is reachable via
            // L.DomUtil.get + .__lmap__ on some builds. Easiest is to
            // walk every DOMnode whose _leaflet is set and pick the
            // one that is an L.Map. This avoids exposing the module's
            // private `map` variable just for tests.
            let mapObj = null;
            for (const el of document.querySelectorAll('.leaflet-container')) {
                // Each map registers itself as the _leaflet_id holder.
                // Iterate Leaflet's internal map cache (window L.Util has
                // _lastId; map instances keep a back-reference).
                if (el._leaflet_id != null && el._leaflet_map) { mapObj = el._leaflet_map; break; }
            }
            // Fallback: Leaflet's own L.Map.fire is what the real
            // handler invokes from the contextmenu listener. We just
            // need ANY route to that handler. The live document
            // already has a contextmenu listener attached by initMap;
            // dispatch a synthetic event to its container.
            const container = document.querySelector('.leaflet-container');
            if (container) {
                const ev = new MouseEvent('contextmenu', {
                    bubbles: true, cancelable: true,
                    clientX: 100, clientY: 100,
                });
                container.dispatchEvent(ev);
            }
            const dotsAfter = document.querySelectorAll('.ona-measure-dot').length;
            const cursor = getComputedStyle(document.querySelector('.leaflet-container')).cursor;
            return { dotsBefore, dotsAfter, cursor };
        });
        expect(result.dotsBefore).toBe(2);
        expect(result.dotsAfter).toBe(0);
        // Still in measure mode after the reset.
        expect(result.cursor).toBe('crosshair');
    });

    test('clearMeasure removes all measure layers', async ({ page }) => {
        const beforeAfter = await page.evaluate(async () => {
            const mod = await import('/_content/OnaPlotter/js/leafletInterop.js')
                .catch(() => import('/js/leafletInterop.js'));
            mod.updatePosition(48.0, 7.85, 0, 0, 0);
            mod.measureFromVesselTo(48.01, 7.86);
            const before = document.querySelectorAll(
                '.ona-measure-dot, .ona-measure-line').length;
            mod.clearMeasure();
            const after = document.querySelectorAll(
                '.ona-measure-dot, .ona-measure-line').length;
            return { before, after };
        });
        expect(beforeAfter.before).toBe(3); // 2 dots + 1 line
        expect(beforeAfter.after).toBe(0);
    });
});
