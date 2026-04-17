// Basic smoke test: walk every page, open every top-level panel, and
// assert the Blazor error UI never shows and no page errors surface.

import { test, expect } from '@playwright/test';
import { collectErrors, waitForMapReady } from './helpers.js';

// Relative paths so they compose with BASE_URL's subpath
// (e.g. https://openplotter.local/signalk-onaplotter/).
const ROUTES = ['', 'map', 'gauges', 'sailsteer', 'wind', 'history', 'raw', 'settings'];

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

    await page.goto('map');
    await waitForMapReady(page);

    // Re-fetch buttons every iteration; some clicks (e.g. Route) remove
    // themselves from the control bar when toggled, which invalidates any
    // captured ElementHandle from .all(). Keep a visited-text set so we
    // don't infinite-loop on sticky toggles.
    const clicked = new Set();
    for (let guard = 0; guard < 30; guard++) {
        const buttons = await page.locator('.map-controls .ctrl-btn').all();
        const next = await Promise.all(buttons.map(async (b) => ({
            handle: b,
            label: ((await b.textContent()) ?? '').trim()
        })));
        const target = next.find(b => !clicked.has(b.label));
        if (!target) break;
        clicked.add(target.label);

        await target.handle.click({ timeout: 3000 }).catch(() => {});
        await page.waitForTimeout(200);
        await assertBlazorErrorNotVisible();
    }
    expect(clicked.size).toBeGreaterThan(5);

    if (errors.length > 0) {
        throw new Error('Console errors clicking control bar:\n' + errors.join('\n'));
    }
});

test('layers panel open/close does not crash', async ({ page }) => {
    const { assertBlazorErrorNotVisible } = collectErrors(page);

    await page.goto('map');
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
