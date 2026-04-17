// Basic smoke test: walk every page, open every top-level panel, and
// assert the Blazor error UI never shows and no page errors surface.

import { test, expect } from '@playwright/test';
import { collectErrors, waitForMapReady } from './helpers.js';

const ROUTES = ['/', '/map', '/gauges', '/sailsteer', '/windrose', '/history', '/rawstream', '/settings'];

test('every top-level page renders without crashing', async ({ page }) => {
    const { errors, assertBlazorErrorNotVisible } = collectErrors(page);

    for (const route of ROUTES) {
        await page.goto(route);
        await waitForMapReady(page);
        await assertBlazorErrorNotVisible();
    }

    if (errors.length > 0) {
        throw new Error('Console errors during navigation:\n' + errors.join('\n'));
    }
});

test('map control bar buttons all click without crashing', async ({ page }) => {
    const { errors, assertBlazorErrorNotVisible } = collectErrors(page);

    await page.goto('/map');
    await waitForMapReady(page);

    // Every .ctrl-btn in the control bar should be clickable and not crash.
    const buttons = await page.locator('.map-controls .ctrl-btn').all();
    expect(buttons.length).toBeGreaterThan(0);

    for (const btn of buttons) {
        await btn.click({ trial: false });
        await page.waitForTimeout(150);
        await assertBlazorErrorNotVisible();
    }

    if (errors.length > 0) {
        throw new Error('Console errors clicking control bar:\n' + errors.join('\n'));
    }
});

test('layers panel open/close does not crash', async ({ page }) => {
    const { assertBlazorErrorNotVisible } = collectErrors(page);

    await page.goto('/map');
    await waitForMapReady(page);

    const layersBtn = page.locator('.ctrl-btn:has-text("Layers")');
    await layersBtn.click();
    await page.waitForTimeout(250);
    await assertBlazorErrorNotVisible();

    // Close again.
    await layersBtn.click();
    await page.waitForTimeout(250);
    await assertBlazorErrorNotVisible();
});
