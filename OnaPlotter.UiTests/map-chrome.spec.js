// Map chrome guard.
//
// Pins the bottom-corner readouts the helm relies on for "what scale
// am I at?": Leaflet's metric scale bar, OnaPlotter's NauticalScale
// (nm / cbl / m) sibling, and the "z14" zoom badge. All three were
// rebuilt under the "Zoom level + 1 nm scale indicator" item in
// Plotter TODO; the badge had been hidden in CSS and the scale bars
// commented out in JS. This spec stops a future cleanup pass from
// silently dropping any of them again.
//
// Run with:
//   BASE_URL=http://localhost:5282 \
//     npx playwright test map-chrome.spec.js

import { test, expect } from '@playwright/test';

test.describe('Map chrome', () => {
    test.beforeEach(async ({ page }) => {
        // Pre-dismiss first-run modals so the welcome card doesn't sit
        // over the map and confuse the test.
        await page.addInitScript(() => {
            localStorage.setItem('ona.hints.welcome.v1.dismissed', '1');
            localStorage.setItem('ona.hints.mapLongPress.dismissed', '1');
        });
        // No SignalK server in this spec; route the WS so the Blazor
        // app doesn't enter its reconnect loop and stays render-idle.
        await page.routeWebSocket(/.*\/signalk\/v1\/stream.*/, ws => {
            ws.onMessage(() => {});
        });
        await page.route(/\/signalk\/v\d+\/api\/(resources|vessels)\/.*/, route => {
            route.fulfill({ status: 200, contentType: 'application/json', body: '{}' });
        });
        await page.goto('/map');
        await page.waitForSelector('.leaflet-container', { timeout: 15000 });
    });

    test('zoom badge is visible and reflects current zoom', async ({ page }) => {
        const badge = page.locator('.ona-zoom-badge');
        await expect(badge).toBeVisible();
        // Format is "zN" without spaces (per ZoomBadge.update()).
        await expect(badge).toHaveText(/^z\d+$/);
    });

    test('metric scale line renders', async ({ page }) => {
        // Leaflet's built-in scale bar gets two lines (metric +
        // imperial); we configured imperial: false so only the metric
        // one should be present from the bundled control. The
        // NauticalScale subclass adds its own .ona-scale-nm sibling.
        const metric = page.locator('.leaflet-control-scale-line').filter({
            hasNotText: /nm|cbl|m$/  // metric line shows "100 m" / "1 km" -- exclude the nautical match
        });
        // Easier: just count that AT LEAST one scale line exists
        // overall. The css class is shared between the bundled metric
        // line and our nautical sibling (they only differ by the
        // .ona-scale-nm modifier on the latter).
        const allLines = page.locator('.leaflet-control-scale-line');
        await expect(allLines.first()).toBeVisible();
        const count = await allLines.count();
        // Metric (built-in) + nautical (NauticalScale subclass) = 2.
        expect(count).toBeGreaterThanOrEqual(2);
    });

    test('nautical scale line uses the .ona-scale-nm modifier', async ({ page }) => {
        // The nautical-miles line is the one we hand-rolled to render
        // nm / cbl / m depending on zoom. Pinning the class so the
        // styling targets stay valid (amber border + warm text).
        const nm = page.locator('.leaflet-control-scale-line.ona-scale-nm');
        await expect(nm).toBeVisible();
        // Content must be a non-empty unit string (numeric + space + unit).
        const text = await nm.textContent();
        expect((text || '').trim()).toMatch(/^\d+(\.\d+)?\s+(m|cbl|nm)$/);
    });

    test('zoom badge updates when the map zooms', async ({ page }) => {
        const badge = page.locator('.ona-zoom-badge');
        const before = await badge.textContent();
        await page.evaluate(async () => {
            const mod = await import('/_content/OnaPlotter/js/leafletInterop.js')
                .catch(() => import('/js/leafletInterop.js'));
            // zoomIn(2) = +2 zoom levels via the public export.
            mod.zoomIn(2);
        });
        // Leaflet animates zoom; wait briefly for the zoomend event
        // that drives the badge update.
        await page.waitForTimeout(400);
        const after = await badge.textContent();
        expect(after).not.toBe(before);
        expect((after || '').trim()).toMatch(/^z\d+$/);
    });
});
