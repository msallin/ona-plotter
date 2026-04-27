// Active-route navigate-back guard.
//
// User report: "I have a route active and an anchor down. I navigate
// to Dashboard and back to /map -- the route is silently deactivated
// on the SignalK server (other plotters lose it too)."
//
// Root cause: Map.razor.SyncServerAnchorAsync used a per-Map-instance
// `serverAnchorDrawn` flag to detect a fresh anchor drop, and on the
// false branch fired CourseApi.ClearAsync() to dismiss any co-active
// course. The flag resets on every Map mount, so a session with both
// states already active on the SK server interpreted each navigate-
// back as a fresh anchor drop and DELETEd the course. The fix drops
// the auto-clear; the manual Anchor button still clears the course
// when the helm taps it explicitly.
//
// This spec mocks both an active anchor (via a WebSocket delta on
// connect) and an active course (via the v2 navigation/course REST
// seed), then asserts that:
//   1. The active polyline draws on first mount.
//   2. After Map -> Dashboard -> Map, no DELETE was issued against
//      /v2/api/vessels/self/navigation/course.
//   3. The active polyline is still present on the second mount.
//
// Run with:
//   BASE_URL=http://localhost:5282 \
//     npx playwright test active-route-nav.spec.js

import { test, expect } from '@playwright/test';

const ROUTE_ID = 'abc-123';
const ROUTE_HREF = `/resources/routes/${ROUTE_ID}`;
const COURSE_BODY = JSON.stringify({
    activeRoute: {
        href: ROUTE_HREF,
        name: 'Test Passage',
        pointIndex: 0,
        pointTotal: 3
    },
    nextPoint: {
        type: 'RoutePoint',
        position: { latitude: 48.05, longitude: 7.86 }
    },
    previousPoint: {
        type: 'VesselPosition',
        position: { latitude: 48.0, longitude: 7.85 }
    }
});
const ROUTE_BODY = JSON.stringify({
    feature: {
        type: 'Feature',
        geometry: {
            type: 'LineString',
            coordinates: [[7.85, 48.0], [7.86, 48.05], [7.87, 48.10]]
        },
        properties: { name: 'Test Passage' }
    }
});

// Anchor delta we'll inject over the WebSocket once the client
// finishes its subscription handshake. Mirrors what
// signalk-anchoralarm-plugin emits when an anchor is already down.
const ANCHOR_DELTA = JSON.stringify({
    context: 'vessels.self',
    updates: [{
        timestamp: '2026-04-27T14:00:00Z',
        values: [
            { path: 'navigation.anchor.position', value: { latitude: 48.0, longitude: 7.85 } },
            { path: 'navigation.anchor.maxRadius', value: 30 }
        ]
    }]
});

const ACTIVE_ROUTE_COLOURS = ['#06b6d4', '#ff9a6b'];

async function activeRouteVisible(page) {
    return await page.evaluate(colours => {
        const paths = document.querySelectorAll('.leaflet-overlay-pane path');
        return Array.from(paths).some(p => {
            const stroke = (p.getAttribute('stroke') || '').toLowerCase();
            return colours.includes(stroke);
        });
    }, ACTIVE_ROUTE_COLOURS);
}

test.describe('Active route survives page navigation', () => {
    // WebKit's Blazor WASM HttpClient choked on Playwright's request
    // route fulfilment ("TypeError: Load failed" for the /v2/api/...
    // /navigation/course seed in this test environment), so the
    // mocked SK seed never lands and the rest of the assertions are
    // vacuous. The bug being pinned is browser-agnostic (the C#
    // Map.razor logic doesn't care which engine the user is on);
    // Chromium coverage is enough for regression purposes.
    test.skip(({ browserName }) => browserName === 'webkit',
        'Playwright route fulfilment + Blazor WASM HttpClient race on WebKit');

    test.beforeEach(async ({ page }) => {
        await page.addInitScript(() => {
            localStorage.setItem('ona.hints.welcome.v1.dismissed', '1');
            localStorage.setItem('ona.hints.mapLongPress.dismissed', '1');
        });
        // The WebSocket mock pushes the anchor delta after a short
        // delay so SignalkClient has time to send its subscribe
        // message and start its receive loop.
        await page.routeWebSocket(/.*\/signalk\/v1\/stream.*/, ws => {
            ws.onMessage(() => {});
            setTimeout(() => {
                try { ws.send(ANCHOR_DELTA); } catch (_) { /* socket closed */ }
            }, 200);
        });
        // Order matters: register the catch-all FIRST and the
        // specific routes LAST so Playwright's reverse-order match
        // gives the bespoke routes priority.
        await page.route(/\/signalk\/v\d+\/api\/(resources|vessels)\/.*/, route => {
            route.fulfill({ status: 200, contentType: 'application/json', body: '{}' });
        });
        await page.route(/\/signalk\/v2\/api\/vessels\/self\/navigation\/course$/, route => {
            route.fulfill({ status: 200, contentType: 'application/json', body: COURSE_BODY });
        });
        await page.route(new RegExp(`/signalk/v[12]/api/resources/routes/${ROUTE_ID}\\b.*`), route => {
            route.fulfill({ status: 200, contentType: 'application/json', body: ROUTE_BODY });
        });
    });

    test('active polyline + anchor coexist on first mount', async ({ page }) => {
        // Sanity: the bug only matters because both states can land
        // simultaneously. Pin the happy path before we exercise the
        // navigation flow. The anchor circle is added to the same
        // overlay pane via L.circle inside setAnchor; we look for any
        // path with the anchor stroke colour family. (--map-anchor-ok
        // / --map-anchor-drag depend on inside/outside but both are in
        // the "anchor" family the helm reads as "set anchor".)
        await page.goto('/map');
        await page.waitForSelector('.leaflet-container', { timeout: 15000 });
        await expect.poll(() => activeRouteVisible(page), { timeout: 10000 }).toBe(true);

        // Confirm the WS-injected anchor delta actually populated
        // NavigationData and the JS side rendered the circle. If this
        // fails the regression test below would be vacuous (no anchor
        // ⇒ no auto-clear path ⇒ the assertion would pass for the
        // wrong reason).
        const anchorRendered = await page.evaluate(() => {
            const paths = document.querySelectorAll('.leaflet-overlay-pane path');
            // The anchor is drawn as an L.circle (filled circle)
            // distinguishable by a non-zero fill on a circular SVG d=
            // attribute. We check for any path with a non-trivial
            // arc (a/A command) that isn't the boat or a measure dot.
            return Array.from(paths).some(p => {
                const fill = (p.getAttribute('fill') || '').toLowerCase();
                const d = p.getAttribute('d') || '';
                return d.includes('a') && fill !== 'none' && fill !== '#ff9a6b';
            });
        });
        expect(anchorRendered).toBe(true);
    });

    test('navigate Map -> Dashboard -> Map does not DELETE the course', async ({ page }) => {
        // Track every DELETE the page sends to the SK course endpoint.
        // Pre-fix this would fire from SyncServerAnchorAsync's
        // serverAnchorDrawn branch on every Map mount whenever both
        // anchor + course were active server-side.
        const courseDeletes = [];
        page.on('request', req => {
            if (req.method() === 'DELETE'
                && /\/signalk\/v\d+\/api\/vessels\/self\/navigation\/course/.test(req.url())) {
                courseDeletes.push(req.url());
            }
        });

        await page.goto('/map');
        await page.waitForSelector('.leaflet-container', { timeout: 15000 });
        await expect.poll(() => activeRouteVisible(page), { timeout: 10000 }).toBe(true);

        await page.click('a[title="Dashboard"]');
        await page.waitForURL(url => !url.pathname.startsWith('/map'), { timeout: 5000 });

        await page.click('a[title="Chart"]');
        await page.waitForURL(/\/map$/, { timeout: 5000 });
        await page.waitForSelector('.leaflet-container', { timeout: 15000 });
        await expect.poll(() => activeRouteVisible(page), { timeout: 10000 }).toBe(true);

        // The crux of the regression test: nothing in the navigate-
        // back flow should issue a DELETE against the course endpoint.
        // The manual Anchor button is the only legitimate path for the
        // auto-clear, and we never touched it in this scenario.
        expect(courseDeletes).toEqual([]);
    });
});
