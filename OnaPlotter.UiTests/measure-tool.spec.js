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
        const colors = await page.evaluate(async () => {
            const mod = await import('/_content/OnaPlotter/js/leafletInterop.js')
                .catch(() => import('/js/leafletInterop.js'));
            mod.updatePosition(48.0, 7.85, 0, 0, 0);
            mod.measureFromVesselTo(48.01, 7.86);
            const dots = Array.from(document.querySelectorAll('.ona-measure-dot'));
            const lines = Array.from(document.querySelectorAll('.ona-measure-line'));
            return [...dots, ...lines].map(p => p.getAttribute('stroke'));
        });
        // The unified colour is #e2e8f0 (slate-200, the same hue the old
        // vessel-to-point bearing line used). Both the dots and the
        // segment line should report this exact stroke.
        expect(colors.length).toBeGreaterThan(0);
        for (const c of colors) {
            expect((c || '').toLowerCase()).toBe('#e2e8f0');
        }
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
