// Fuzz test: clicks a random sequence of buttons on the map page using a
// seeded PRNG and asserts nothing ever triggers the Blazor error UI or
// throws on the page. Set FUZZ_SEED=<int> to reproduce a specific run.
//
// Deliberately does NOT drag-pan the map, start route edit, or activate MOB
// (those mutate server state). Safe to run against a live openplotter.

import { test } from '@playwright/test';
import { collectErrors, waitForMapReady, seededRandom } from './helpers.js';

const FUZZ_SEED = parseInt(process.env.FUZZ_SEED ?? `${Date.now() & 0x7fffffff}`, 10);
const FUZZ_CLICKS = parseInt(process.env.FUZZ_CLICKS ?? '40', 10);

// Selectors the fuzzer is allowed to click. Each is considered non-destructive.
const SAFE_CLICK_SELECTORS = [
    '.map-controls .ctrl-btn:has-text("Follow")',
    '.map-controls .ctrl-btn:has-text("Free")',
    '.map-controls .ctrl-btn:has-text("N-Up"), .ctrl-btn:has-text("C-Up"), .ctrl-btn:has-text("H-Up")',
    '.map-controls .ctrl-btn:has-text("Night")',
    '.map-controls .ctrl-btn:has-text("Fit")',
    '.map-controls .ctrl-btn:has-text("Laylines")',
    '.map-controls .ctrl-btn:has-text("Layers")',
    '.chart-quick-chip',
    '.chart-item .chart-item-body',
    '.vessel-row',
    '.section-toggle',
];

// Title kept static so Playwright's worker process can re-attach to the
// test between retries. Seed is still deterministic via FUZZ_SEED, logged
// below so a caught failure can be reproduced.
test('fuzz map UI', async ({ page }) => {
    console.log(`FUZZ_SEED=${FUZZ_SEED} FUZZ_CLICKS=${FUZZ_CLICKS}`);
    const { errors, assertBlazorErrorNotVisible } = collectErrors(page);
    const rng = seededRandom(FUZZ_SEED);

    await page.goto('map');
    await waitForMapReady(page);

    for (let i = 0; i < FUZZ_CLICKS; i++) {
        const selector = SAFE_CLICK_SELECTORS[Math.floor(rng() * SAFE_CLICK_SELECTORS.length)];
        const candidates = await page.locator(selector).all();
        if (candidates.length === 0) continue;
        const target = candidates[Math.floor(rng() * candidates.length)];
        try {
            await target.click({ timeout: 2000, force: false });
        } catch {
            // Element might have disappeared mid-click (common with panels that
            // re-render on toggle). Not a crash; continue.
        }
        await page.waitForTimeout(80 + Math.floor(rng() * 120));
        await assertBlazorErrorNotVisible();
    }

    if (errors.length > 0) {
        throw new Error(
            `Console errors during fuzz (seed ${FUZZ_SEED}):\n` + errors.join('\n'));
    }
});
