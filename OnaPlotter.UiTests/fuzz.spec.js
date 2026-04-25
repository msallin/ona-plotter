// Fuzz test: clicks a random sequence of buttons on the map page using a
// seeded PRNG and asserts nothing ever triggers the Blazor error UI or
// throws on the page. Set FUZZ_SEED=<int> to reproduce a specific run.
//
// Deliberately does NOT drag-pan the map, start route edit, or activate MOB
// (those mutate server state). Safe to run against a live openplotter.

import { test } from '@playwright/test';
import { collectErrors, waitForMapReady, seededRandom } from './helpers.js';

const FUZZ_SEED = parseInt(process.env.FUZZ_SEED ?? `${Date.now() & 0x7fffffff}`, 10);
// Local-default click budget. CI overrides via FUZZ_CLICKS in ci.yml --
// see the per-click cost note above the test.setTimeout call below.
const FUZZ_CLICKS = parseInt(process.env.FUZZ_CLICKS ?? '40', 10);

// Selectors the fuzzer is allowed to click. Each is considered
// non-destructive: it never mutates server-side resources, never
// engages a flow that requires user input to exit (route-edit,
// polygon draw), and never triggers a safety-critical state change
// (anchor drop, MOB, Stop Navigation). Add-menu items render with
// .ctrl-more-item now (same shape as the More overflow), so a single
// .ctrl-more-item entry covers both flyouts.
const SAFE_CLICK_SELECTORS = [
    '.map-controls .ctrl-btn:has-text("Follow")',
    '.map-controls .ctrl-btn:has-text("Free")',
    '.map-controls .ctrl-btn[title*="orientation"]',
    '.map-controls .ctrl-btn:has-text("Fit")',
    '.map-controls .ctrl-btn:has-text("Centre")',
    '.map-controls .ctrl-btn:has-text("Layers")',
    '.map-controls .ctrl-btn:has-text("More")',
    '.map-controls .ctrl-btn:has-text("Add")',   // opens Add flyout
    '.map-controls .ctrl-btn:has-text("Race")',  // race-mode only; 0 candidates skips
    '.ctrl-more-item',                           // More + Add flyout entries
    '.chart-quick-chip',
    '.chart-item .chart-item-body',
    '.chart-active-chip .chart-reorder-btn',
    '.vessel-row',
    '.section-toggle',
    '.notes-visible-toggle',                     // toggles notes layer (idempotent)
    '.hud-top-left .hud-panel',                  // HUD click-to-expand
    '.hud-top-right .hud-panel',
    '.hud-bottom-left .hud-panel',
    '.hud-bottom-right .hud-panel',
];

// Title kept static so Playwright's worker process can re-attach to the
// test between retries. Seed is still deterministic via FUZZ_SEED, logged
// below so a caught failure can be reproduced.
test('fuzz map UI', async ({ page }) => {
    // The per-click budget is bounded (timeout: 2000 + 80-200 ms wait).
    // CI runs 200 clicks (FUZZ_CLICKS in ci.yml) which uses most of the
    // 3-min cap on a healthy runner: average ~0.4 s per click since
    // most selectors have 1-N candidates and click in well under the
    // 2 s timeout. Worst case (every click chews its full 2 s) is
    // bounded by this cap rather than Playwright's default 60 s.
    test.setTimeout(3 * 60 * 1000);
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
