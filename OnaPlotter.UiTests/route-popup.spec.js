// Route popup guards.
//
// Two TODO items pinned here:
//
// 1) Two-step delete confirm shows "Really?" (was "Really delete?").
//    The longer string changed the button width on first tap, which
//    bumped the surrounding popup layout under the helm's finger.
//    Pinning the new label so a future cleanup pass can't quietly
//    revert to the wider variant.
//
// 2) Tapping the active-route polyline opens a popup with Deactivate
//    / Edit / Delete. Pre-fix the active overlay had no popup at all
//    -- the helm could only deactivate from the bottom-bar Stop button
//    or the right-click context menu, both far from the polyline they
//    were already pointing at.
//
// Both items exercise the Leaflet popup wiring directly via
// addRoute / setActiveRoute, so the spec doesn't need a SignalK
// server.
//
// Run with:
//   BASE_URL=http://localhost:5282 \
//     npx playwright test route-popup.spec.js

import { test, expect } from '@playwright/test';

const COORDS = [
    [48.0, 7.85],
    [48.05, 7.86],
    [48.10, 7.87]
];

test.describe('Route popups', () => {
    test.beforeEach(async ({ page }) => {
        await page.addInitScript(() => {
            localStorage.setItem('ona.hints.welcome.v1.dismissed', '1');
            localStorage.setItem('ona.hints.mapLongPress.dismissed', '1');
        });
        await page.routeWebSocket(/.*\/signalk\/v1\/stream.*/, ws => {
            ws.onMessage(() => {});
        });
        await page.route(/\/signalk\/v\d+\/api\/(resources|vessels)\/.*/, route => {
            route.fulfill({ status: 200, contentType: 'application/json', body: '{}' });
        });
        await page.goto('/map');
        await page.waitForSelector('.leaflet-container', { timeout: 15000 });
    });

    test('regular route delete confirm shows "Really?" then fires DeleteRouteById', async ({ page }) => {
        const result = await page.evaluate(async (coords) => {
            const mod = await import('/_content/OnaPlotter/js/leafletInterop.js')
                .catch(() => import('/js/leafletInterop.js'));
            mod.fitBounds(47.5, 7.5, 49.5, 8.5);
            mod.addRoute('test-route', 'Test Route', coords);
            // Open the popup by simulating a click on the visible
            // polyline (Leaflet exposes openPopup on layers; bypass
            // the click by reaching into the route layer directly).
            // Easier: trigger popupopen manually via the layer's
            // openPopup() API. We need a reference to the line; the
            // module doesn't expose one, so we open via the DOM by
            // finding the polyline path and dispatching a click.
            const paths = document.querySelectorAll('.leaflet-overlay-pane path');
            // The hit polyline is wider (weight 36) -- it shows as a
            // path with stroke-width 36. Click it to open the popup.
            const hit = Array.from(paths).find(p => p.getAttribute('stroke-width') === '36');
            if (!hit) return { found: false };
            hit.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
            // Wait a tick for Leaflet to render the popup.
            await new Promise(r => setTimeout(r, 50));
            const btn = document.querySelector('.route-popup .route-delete-btn');
            if (!btn) return { found: false };
            const initialText = btn.textContent;
            btn.click();
            // The first click should swap to "Really?" without firing
            // any C# invocation.
            const confirmText = btn.textContent;
            const confirmingClass = btn.classList.contains('confirming');
            return { found: true, initialText, confirmText, confirmingClass };
        }, COORDS);
        expect(result.found).toBe(true);
        expect(result.initialText).toBe('Delete');
        // Crux of the assertion: short label, no layout shift.
        expect(result.confirmText).toBe('Really?');
        expect(result.confirmingClass).toBe(true);
    });

    test('active route polyline opens Deactivate / Edit / Delete popup', async ({ page }) => {
        const result = await page.evaluate(async (coords) => {
            const mod = await import('/_content/OnaPlotter/js/leafletInterop.js')
                .catch(() => import('/js/leafletInterop.js'));
            mod.fitBounds(47.5, 7.5, 49.5, 8.5);
            // setActiveRoute(coords, wpIdx, routeId, routeName) --
            // wpIdx=1 means "next WP is index 1" so coords.slice(0,1)
            // is dotted (passed) and coords.slice(0) is solid (future).
            mod.setActiveRoute(coords, 1, 'active-route-id', 'Active Test');
            // Wait for the layer to render.
            await new Promise(r => setTimeout(r, 50));
            // Find the hit polyline (weight 36 transparent) inside
            // the active-route layer and click it.
            const paths = document.querySelectorAll('.leaflet-overlay-pane path');
            const hit = Array.from(paths).find(p => p.getAttribute('stroke-width') === '36');
            if (!hit) return { foundHit: false };
            hit.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
            await new Promise(r => setTimeout(r, 50));
            const popup = document.querySelector('.route-popup');
            if (!popup) return { foundHit: true, foundPopup: false };
            return {
                foundHit: true,
                foundPopup: true,
                title: popup.querySelector('.route-popup-title')?.textContent,
                meta: popup.querySelector('.route-popup-meta')?.textContent,
                buttons: Array.from(popup.querySelectorAll('button')).map(b => b.textContent),
            };
        }, COORDS);
        expect(result.foundHit).toBe(true);
        expect(result.foundPopup).toBe(true);
        expect(result.title).toBe('Active Test');
        expect(result.meta).toContain('active');
        expect(result.buttons).toEqual(['Deactivate', 'Edit', 'Delete']);
    });

    test('Deactivate click closes popup and is single-tap (no two-step confirm)', async ({ page }) => {
        // Stop-the-active-course is non-destructive (the route resource
        // stays; only the SignalK course is cleared), so a two-step
        // confirm would be friction. Mirror behaviour pinned: click
        // closes the popup immediately, no "Really?" intermediate.
        // A regression that copy-pasted the delete two-step onto
        // Deactivate would surface here as the popup still being open
        // after the first click.
        const result = await page.evaluate(async (coords) => {
            const mod = await import('/_content/OnaPlotter/js/leafletInterop.js')
                .catch(() => import('/js/leafletInterop.js'));
            mod.fitBounds(47.5, 7.5, 49.5, 8.5);
            mod.setActiveRoute(coords, 1, 'active-route-id', 'Active Test');
            await new Promise(r => setTimeout(r, 50));
            const paths = document.querySelectorAll('.leaflet-overlay-pane path');
            const hit = Array.from(paths).find(p => p.getAttribute('stroke-width') === '36');
            hit?.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
            await new Promise(r => setTimeout(r, 50));
            const btn = document.querySelector('.route-popup .route-deactivate-btn');
            if (!btn) return { foundBtn: false };
            const labelBeforeClick = btn.textContent;
            btn.click();
            // Tick for popup teardown.
            await new Promise(r => setTimeout(r, 80));
            return {
                foundBtn: true,
                labelBeforeClick,
                popupGoneAfterClick: !document.querySelector('.route-popup'),
            };
        }, COORDS);

        expect(result.foundBtn).toBe(true);
        // Single-tap label -- not "Really?" or any confirming variant.
        expect(result.labelBeforeClick).toBe('Deactivate');
        expect(result.popupGoneAfterClick).toBe(true);
    });
});
