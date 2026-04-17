// Click each control-bar button independently (reload between each) and
// log console/page errors plus the Blazor error banner state. Locates the
// exact button that triggers a crash.

import { test } from '@playwright/test';

test.setTimeout(240_000);

const BUTTON_LABELS = [
    'Free', 'Follow',      // Follow toggle (either label)
    'N-Up', 'C-Up', 'H-Up', // Orientation
    'Night',
    'Anchor',
    'MOB',
    'Fit',
    'Laylines',
    'Route',
    'Race',
    'Layers',
];

test('click each map control-bar button', async ({ page }) => {
    for (const label of BUTTON_LABELS) {
        const pageErrors = [];
        const consoleErrors = [];
        const onPageError = (e) => pageErrors.push(e.message);
        const onConsole = (msg) => {
            if (msg.type() === 'error') consoleErrors.push(msg.text());
        };
        page.on('pageerror', onPageError);
        page.on('console', onConsole);

        await page.goto('map');
        await page.waitForSelector('#app > *:not(:empty)');
        await page.waitForTimeout(2000);

        const btn = page.locator(`.map-controls .ctrl-btn:has-text("${label}")`).first();
        if (!(await btn.isVisible().catch(() => false))) {
            console.log(`  "${label}" not visible, skipping`);
            page.off('pageerror', onPageError);
            page.off('console', onConsole);
            continue;
        }

        try {
            await btn.click({ timeout: 3000 });
            await page.waitForTimeout(500);
        } catch (e) {
            console.log(`  "${label}" click threw: ${e.message}`);
        }

        const bannerVisible = await page.locator('#blazor-error-ui').isVisible().catch(() => false);
        const marker = bannerVisible || pageErrors.length > 0 ? ' *** CRASH ***' : '';
        console.log(`  "${label}"${marker}`);
        for (const e of pageErrors) console.log('    pageerror: ' + e);
        for (const e of consoleErrors.filter(x => !x.includes('404'))) console.log('    console  : ' + e.slice(0, 300));

        page.off('pageerror', onPageError);
        page.off('console', onConsole);
    }
});
