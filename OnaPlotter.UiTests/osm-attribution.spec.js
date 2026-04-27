// OSM attribution leak guard.
//
// Pins the behaviour added under the "OSM attribution leaks when OSM
// is inactive" item in Plotter TODO: when the user enables a SignalK
// chart-tiles base, the OSM + OpenSeaMap layers are removed so the
// "OpenStreetMap contributors" attribution string disappears from the
// bottom-right control. When the last chart layer is removed, OSM /
// OpenSeaMap come back as the fallback basemap.
//
// Tests at the JS-interop layer (window.* exports from
// leafletInterop.js) so we exercise the actual addChartLayer /
// removeChartLayer flow Blazor uses, not a synthetic Leaflet stub.
//
// Run with:
//   BASE_URL=http://localhost:5282 \
//     npx playwright test osm-attribution.spec.js

import { test, expect } from '@playwright/test';

test.describe('OSM attribution', () => {
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
        await page.waitForSelector('.leaflet-control-attribution', { timeout: 15000 });
    });

    test('OSM + OpenSeaMap shown by default', async ({ page }) => {
        const text = await page.locator('.leaflet-control-attribution').textContent();
        expect(text).toContain('OpenStreetMap');
        expect(text).toContain('OpenSeaMap');
    });

    test('attribution clears when a SignalK chart base layer is added', async ({ page }) => {
        // The leafletInterop module is loaded into the Blazor JS runtime
        // and exposed by Map.razor. Fish it out of the dynamic import map
        // instead of relying on a window.* shim.
        await page.evaluate(async () => {
            const mod = await import('/_content/OnaPlotter/js/leafletInterop.js')
                .catch(() => import('/js/leafletInterop.js'));
            mod.addChartLayer(
                'test-chart',
                'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNgAAIAAAUAAeImBZsAAAAASUVORK5CYII=',
                1, 18, 1.0, null);
        });
        // Allow Leaflet's attribution control a tick to update.
        await page.waitForTimeout(200);
        const text = await page.locator('.leaflet-control-attribution').textContent();
        expect(text || '').not.toContain('OpenStreetMap');
        expect(text || '').not.toContain('OpenSeaMap');
    });

    test('attribution comes back when the last chart layer is removed', async ({ page }) => {
        await page.evaluate(async () => {
            const mod = await import('/_content/OnaPlotter/js/leafletInterop.js')
                .catch(() => import('/js/leafletInterop.js'));
            mod.addChartLayer(
                'test-chart-2',
                'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNgAAIAAAUAAeImBZsAAAAASUVORK5CYII=',
                1, 18, 1.0, null);
            mod.removeChartLayer('test-chart-2');
        });
        await page.waitForTimeout(200);
        const text = await page.locator('.leaflet-control-attribution').textContent();
        expect(text).toContain('OpenStreetMap');
        expect(text).toContain('OpenSeaMap');
    });
});
