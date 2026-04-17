// Diagnostic: visit each route, dump console/page errors and the Blazor
// error UI text. Used to locate which navigation triggers the crash the
// user reported. Does not make assertions - just logs.

import { test } from '@playwright/test';

const ROUTES = ['', 'map', 'gauges', 'sailsteer', 'wind', 'history', 'raw', 'settings'];

test('diagnose per-page', async ({ page }) => {
    for (const route of ROUTES) {
        const errors = [];
        const onError = (e) => errors.push(`pageerror: ${e.message}\n${e.stack ?? ''}`);
        const onConsole = (msg) => {
            if (msg.type() === 'error') errors.push(`console: ${msg.text()}`);
        };
        page.on('pageerror', onError);
        page.on('console', onConsole);

        try {
            await page.goto(route, { waitUntil: 'networkidle', timeout: 20_000 });
        } catch (e) {
            console.log(`\n=== route '${route}' goto failed: ${e.message}`);
        }

        await page.waitForTimeout(2500);

        const banner = page.locator('#blazor-error-ui');
        const visible = await banner.isVisible().catch(() => false);
        const bannerText = visible ? (await banner.textContent() ?? '').trim() : '';

        console.log(`\n=== route '${route}' ===`);
        console.log(`  error banner: ${visible ? 'VISIBLE -> ' + bannerText : 'ok'}`);
        if (errors.length > 0) {
            console.log('  errors:');
            for (const e of errors.slice(0, 6)) console.log('    ' + e.split('\n').slice(0, 5).join('\n    '));
        }

        page.off('pageerror', onError);
        page.off('console', onConsole);
    }
});
