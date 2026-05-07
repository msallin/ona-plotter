// OSM + OpenSeaMap fallback-layer guard.
//
// OSM (road tiles) and OpenSeaMap (transparent seamark overlay) stay
// attached to the map as permanent fallback layers, even when the
// helm enables a SignalK chart-tiles base. Two reasons:
//   * OSM fills anywhere the SignalK chart tiles fail to fetch
//     (404, network error, beyond chart bounds). Without this the
//     helm would see a blank rectangle for missing tiles.
//   * OpenSeaMap is the seamark overlay (buoys / lights / marinas)
//     that draws on top of any basemap. The single most useful
//     sailing layer.
//
// Earlier versions of leafletInterop.js removed these layers when a
// chart was added - intended as an "OSM attribution leak" fix, but
// the diagnosis was wrong. OSM data IS being used (as fallback) so
// the attribution is correctly shown. Two regressions resulted:
// helm using SignalK charts lost seamarks AND lost the fallback
// for failed tiles. This test pins the corrected contract: OSM and
// OpenSeaMap attributions are always present.
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

    test('OSM + OpenSeaMap stay attached when a SignalK chart layer is added', async ({ page }) => {
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
        // Both attributions remain visible: OSM is the fallback for
        // any tiles the SignalK chart fails to fetch; OpenSeaMap is
        // the always-on seamark overlay.
        expect(text || '').toContain('OpenStreetMap');
        expect(text || '').toContain('OpenSeaMap');
    });

    test('OSM + OpenSeaMap still present after chart add + remove cycle', async ({ page }) => {
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
