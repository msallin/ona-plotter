// HUD layout spec - REAL data variant.
//
// Same viewport / variant matrix as hud-layout.spec.js but does NOT
// mock the SignalK WebSocket or stub the HTTP API. Drives a live
// openplotter.local (or whatever BASE_URL points at) so screenshots
// reflect actual on-boat values rather than the canned 5 kn / 2.0 m
// shallow-alarm scenario the regression spec uses.
//
// Run with:
//   BASE_URL=https://openplotter.local/signalk-onaplotter/ \
//     npx playwright test hud-layout-real.spec.js
//
// Screenshots land in ./screenshots/<project>/hud-real-*.png so they
// don't collide with the regression spec's deterministic set.

import { test, expect } from '@playwright/test';

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

test.describe('HUD layout (real data)', () => {
    for (const vp of VIEWPORTS) {
        test(`${vp.name} (${vp.width}x${vp.height})`, async ({ page }) => {
            // Same first-run dismissals as the regression spec; sidebar
            // collapsed so the map gets the full content area; expandAll
            // wired for the *-details variant. No mocks: data flows
            // straight from the real SignalK server.
            await page.addInitScript(() => {
                localStorage.setItem('ona.hints.welcome.v1.dismissed', '1');
                localStorage.setItem('ona.hints.mapLongPress.dismissed', '1');
                localStorage.setItem('ona.sidebarCollapsed.v1', 'true');
            });

            await page.setViewportSize({ width: vp.width, height: vp.height });
            await page.goto('map');
            await page.waitForSelector('.hud-stack-tl .hud-card',
                { state: 'attached', timeout: 30000 });

            // Wait up to 10 s for the SignalK WS to deliver a first
            // delta so the cards have something other than `--`. If the
            // server is silent we still take the screenshot - the empty
            // state is itself useful evidence.
            await page.waitForFunction(() => {
                const v = document.querySelector('.hud-stack-tl .hud-value');
                return v && !v.textContent.trim().startsWith('-');
            }, { timeout: 10000 }).catch(() => { /* fall through */ });

            if (vp.density) {
                await page.evaluate((cls) => {
                    document.querySelector('.map-container')?.classList.add(cls);
                }, vp.density);
            }
            if (vp.expandAll) {
                await page.evaluate(() => {
                    localStorage.setItem('ona.expandAllHud.v1', 'true');
                });
                await page.reload();
                await page.waitForSelector('.hud-stack-tl .hud-card',
                    { state: 'attached', timeout: 30000 });
                if (vp.density) {
                    await page.evaluate((cls) => {
                        document.querySelector('.map-container')?.classList.add(cls);
                    }, vp.density);
                }
                await page.waitForTimeout(800);
            }

            // Settle map tiles + Blazor render.
            await page.waitForTimeout(1500);

            await page.screenshot({
                path: `screenshots/${test.info().project.name}/hud-real-${vp.name}.png`,
                fullPage: false,
                animations: 'disabled',
            });

            // Sanity check: the four corner stacks should be in the DOM
            // regardless of which paths the live server is publishing.
            const stackClasses = await page.$$eval('.hud-stack', els =>
                els.map(e => e.className.replace(/^hud-stack /, ''))
            );
            expect(stackClasses).toEqual(expect.arrayContaining([
                'hud-stack-tl', 'hud-stack-tr',
                'hud-stack-bl', 'hud-stack-br',
                'hud-stack-bc',
            ]));
        });
    }
});
