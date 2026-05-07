// HUD layout spec.
//
// Captures screenshots of the chartplotter HUD across the device matrix
// (phone portrait/landscape, iPad portrait/landscape, notebook, helm
// console proxy) on Chromium + WebKit. Mocks the SignalK WebSocket so
// the cards render with deterministic values rather than `--`.
//
// The Blazor app connects to ws://localhost:3000/signalk/v1/stream by
// default. We intercept that WS via routeWebSocket() and reply with a
// canned delta containing realistic numbers (~5 kn SOG, 8.4 m depth,
// AWA 35deg / 9 kn AWS, etc). The HUDs read these and render normally.
//
// Run with: BASE_URL=http://localhost:5282 npx playwright test hud-layout.spec.js
// Screenshots land in ./screenshots/. Compare visually before/after a CSS
// change to evaluate the design.

import { test, expect } from '@playwright/test';
import { collectErrors } from './helpers.js';

// Viewport + variant matrix. The first six are the device targets at
// default density (.density-comfortable, no expand-all). The last two
// are 21.5" helm-console variants the user picks via Settings: bigger
// type via .density-helm-console, plus an "expand all HUDs" mode that
// shows every corner card in its expanded state. */
const VIEWPORTS = [
    { name: 'iphone-portrait',           width: 393,  height: 852 },
    { name: 'iphone-landscape',          width: 852,  height: 393 },
    { name: 'ipad-portrait',             width: 834,  height: 1194 },
    { name: 'ipad-landscape',            width: 1194, height: 834 },
    { name: 'notebook',                  width: 1440, height: 900 },
    { name: 'helm-console',              width: 1920, height: 1200 },
    { name: 'helm-console-big',          width: 1920, height: 1200, density: 'density-helm-console' },
    { name: 'helm-console-big-details',  width: 1920, height: 1200, density: 'density-helm-console', expandAll: true },
];

// Canned SignalK delta. Values picked so every HUD card has something to
// show - and shows the *interesting* state that takes the most space /
// widens the card the most. Depth is below the threshold (set via
// localStorage in the spec setup) so the depth card renders the bar in
// alarm colour + warning glyph + threshold label. SOG / COG / wind /
// heading values land in the realistic-cruising-sailboat range.
function buildDelta() {
    return {
        context: 'vessels.self',
        updates: [{
            $source: 'mock',
            timestamp: new Date().toISOString(),
            values: [
                { path: 'navigation.position', value: { latitude: 43.2, longitude: -7.6 } },
                { path: 'navigation.speedOverGround', value: 2.57 },          // ~5.0 kn
                { path: 'navigation.courseOverGroundTrue', value: 2.42 },      // ~138 deg
                { path: 'navigation.headingTrue', value: 2.36 },               // ~135 deg (3 deg drift)
                // Depth 2.0 m - below the 3.0 m default alarm threshold so
                // the depth card shows its loudest variant: red bar fill,
                // warning glyph, depth-danger digit colour. Path matches
                // SignalkClient.SelfFastTierPaths (belowTransducer, not
                // belowKeel which the app doesn't subscribe to).
                { path: 'environment.depth.belowTransducer', value: 2.0 },
                { path: 'environment.wind.angleApparent', value: 0.61 },       // ~35 deg STBD
                { path: 'environment.wind.speedApparent', value: 9.0 },        // ~17.5 kn
                { path: 'environment.wind.angleTrueWater', value: 0.96 },      // ~55 deg STBD
                { path: 'environment.wind.speedTrue', value: 7.5 },            // ~14.6 kn
                // Autopilot engaged in heading mode tracking 140 deg true,
                // current heading 135 deg -> 5 deg HDG err, rudder 3 deg
                // starboard. Lights up the expanded HDG card with AP / AP
                // tgt / HDG err / Rudder rows so the helm-console-big-
                // details screenshot shows them.
                { path: 'steering.autopilot.state', value: 'auto' },
                { path: 'steering.autopilot.target.headingTrue', value: 2.443 }, // ~140 deg
                { path: 'steering.rudderAngle', value: 0.0524 },                 // ~3 deg STBD
                // Tide - next high in 4 h, low after that. The depth
                // card surfaces a "HW 4h 3.2m" line when these values
                // are published. timeHigh/timeLow are ISO-8601 UTC.
                { path: 'environment.tide.heightNow',  value: 1.8 },
                { path: 'environment.tide.heightHigh', value: 3.2 },
                { path: 'environment.tide.heightLow',  value: 0.4 },
                { path: 'environment.tide.timeHigh',   value: new Date(Date.now() + 4 * 3600 * 1000).toISOString() },
                { path: 'environment.tide.timeLow',    value: new Date(Date.now() + 10 * 3600 * 1000).toISOString() },
                { path: 'environment.tide.stationName', value: 'Brest' },
            ],
        }],
    };
}

test.describe('HUD layout', () => {
    for (const vp of VIEWPORTS) {
        test(`${vp.name} (${vp.width}x${vp.height})`, async ({ page }) => {
            const { errors, assertBlazorErrorNotVisible } = collectErrors(page);

            // Pre-set the first-run dismissal flags so the Welcome card +
            // touch coachmark don't show up in any screenshot. Keys match
            // OnaPlotter.Services.LocalStorageKeyValueStore prefix
            // ("ona.") + the keys defined in Map.FirstRun.razor.cs.
            // Also set the depth alarm threshold to 3.0 m so the mocked
            // 2.0 m depth crosses below it and the depth card renders its
            // alarm variant. Sidebar collapsed by default so the map
            // gets the full viewport width.
            await page.addInitScript(() => {
                localStorage.setItem('ona.hints.welcome.v1.dismissed', '1');
                localStorage.setItem('ona.hints.mapLongPress.dismissed', '1');
                localStorage.setItem('ona.depthAlarmThreshold', '3.0');
                localStorage.setItem('ona.sidebarCollapsed.v1', 'true');
            });

            // Stub the SignalK HTTP API endpoints the app fetches on
            // boot (resources/routes, /waypoints, /notes, /regions, etc.).
            // Without these the fetches throw and the toast-stack pile up
            // four error toasts per page load - they obscure the bottom
            // of the screenshot.
            await page.route(/\/signalk\/v\d+\/api\/(resources|vessels)\/.*/, route => {
                route.fulfill({
                    status: 200,
                    contentType: 'application/json',
                    body: JSON.stringify({}),
                });
            });

            // Mock the SignalK WebSocket: the app sends a subscribe payload,
            // we just push the canned delta back. Keep the connection open
            // so the app doesn't enter the reconnect loop that prevents the
            // page from settling.
            await page.routeWebSocket(/.*\/signalk\/v1\/stream.*/, ws => {
                ws.onMessage(() => { /* ignore subscribe acks */ });
                // Brief delay so the app sees the connection established
                // before deltas arrive (matches real-server timing).
                setTimeout(() => ws.send(JSON.stringify(buildDelta())), 100);
            });

            await page.setViewportSize({ width: vp.width, height: vp.height });
            await page.goto('/map');
            // Wait for the actual HUD card inside the TL stack to render.
            // Waiting on the stack itself flakes because flex-end-aligned
            // containers can report 0 width to Playwright's visibility
            // check; the card has content and is reliably visible.
            await page.waitForSelector('.hud-stack-tl .hud-card',
                { state: 'attached', timeout: 15000 });

            // First-run modals are pre-suppressed via addInitScript above;
            // no dismissal needed.

            // Apply optional helm-console variants. The density class is
            // a CSS hook the user picks via Settings; we add it directly
            // for the screenshot. ExpandAllHud is wired via its own KV
            // flag set in addInitScript - but we set it per-test here so
            // the same helm-console viewport produces both variants.
            if (vp.density) {
                await page.evaluate((cls) => {
                    document.querySelector('.map-container')?.classList.add(cls);
                }, vp.density);
            }
            if (vp.expandAll) {
                await page.evaluate(() => {
                    localStorage.setItem('ona.expandAllHud.v1', 'true');
                });
                // Reload so the Settings service reads the flag.
                await page.reload();
                await page.waitForSelector('.hud-stack-tl .hud-card',
                    { state: 'attached', timeout: 15000 });
                if (vp.density) {
                    await page.evaluate((cls) => {
                        document.querySelector('.map-container')?.classList.add(cls);
                    }, vp.density);
                }
                await page.waitForTimeout(500);
            }

            // Wait for at least one HUD value to populate from the mocked
            // delta. If the WS mock isn't wired up correctly, the page
            // still renders with '--' placeholders - screenshot anyway
            // and let the human reviewer spot the empty card.
            await page.waitForFunction(() => {
                const v = document.querySelector('.hud-stack-tl .hud-value');
                return v && !v.textContent.trim().startsWith('-');
            }, { timeout: 5000 }).catch(() => { /* fall through with --s */ });

            // Settle leaflet tile loading + Blazor render.
            await page.waitForTimeout(800);

            await page.screenshot({
                path: `screenshots/${test.info().project.name}/hud-${vp.name}.png`,
                fullPage: false,
                animations: 'disabled',
            });


            // Layout assertion: the four corner stacks plus bottom-center
            // are all in the DOM. Bottom-center has no children unless
            // route or anchor is active; that's fine, it just has h=0.
            const stackClasses = await page.$$eval('.hud-stack', els =>
                els.map(e => e.className.replace(/^hud-stack /, ''))
            );
            expect(stackClasses).toEqual(expect.arrayContaining([
                'hud-stack-tl', 'hud-stack-tr',
                'hud-stack-bl', 'hud-stack-br',
                'hud-stack-bc',
            ]));

            // Blazor error UI must NOT be visible. Console errors are
            // logged but not asserted against - the WS mock handshake
            // and Leaflet tile noise produce benign chatter that varies
            // by browser engine; the visual + layout asserts above are
            // what catch real regressions.
            await assertBlazorErrorNotVisible();
            if (errors.length) console.log(`[${vp.name}] console errors:`, errors);
        });
    }
});
