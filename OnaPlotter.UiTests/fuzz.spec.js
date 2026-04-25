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
//
// Idempotent-toggle rule: every entry must be safe to click N times.
// Pure toggles (Follow / Layers / alarm-log) flip on then off then on,
// landing back at a known state regardless of click count. Modal-
// opening entries (alarm-log button) are paired with a dismissal
// (alarm-log-close + backdrop) so the fuzz can naturally close what
// it opens via a subsequent random click.
const SAFE_CLICK_SELECTORS = [
    // --- Map-page bottom control bar ---
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
    '.ctrl-more-backdrop',                       // closes a left-open More menu

    // --- Layers panel rows (toggling a chart/route is idempotent) ---
    '.chart-quick-chip',
    '.chart-item .chart-item-body',
    '.chart-active-chip .chart-reorder-btn',
    '.vessel-row',
    '.section-toggle',
    '.notes-visible-toggle',                     // toggles notes layer
    'input[type="checkbox"][role="switch"]',     // generic layer-toggle checkboxes (atons / regions / weather / ...)

    // --- HUD click-to-expand (idempotent) ---
    '.hud-top-left .hud-panel',
    '.hud-top-right .hud-panel',
    '.hud-bottom-left .hud-panel',
    '.hud-bottom-right .hud-panel',

    // --- Top row: connection / alarm log / topbar icons ---
    '.connection-retry',                         // idempotent reconnect ping
    '.alarm-log-btn',                            // toggles alarm log
    '.alarm-log-close',                          // closes a left-open log
    '.alarm-log-backdrop',                       // tap-outside-to-close log
    '.alarm-log-handle',                         // mobile bottom-sheet grab handle
    '.topbar-icon-btn[title*="Zoom in"]',        // zoom +1 (clamped at maxZoom)
    '.topbar-icon-btn[title*="Zoom out"]',       // zoom -1 (clamped at minZoom)
    '.topbar-icon-btn[title*="night mode"]',     // Night/Day toggle (idempotent)
    // NOTE: fullscreen toggle deliberately omitted -- headless Chrome's
    // Fullscreen API put the test viewport into a weird state on some
    // CI runs, and Esc doesn't always exit cleanly. Keep it manual.

    // --- Sidebar (cross-page navigation + collapse) ---
    // Navigation links flip the route mid-fuzz, exercising every page's
    // mount path. Subsequent clicks find selectors with 0 candidates
    // (the loop's empty-skip handles that) and a random later click
    // returns to /map. Settings / Dashboard / etc don't render the
    // map-controls so the fuzz doesn't accidentally mutate something
    // there.
    '.sidebar a.nav-link',
    '.nav-collapse-btn',                         // sidebar icon-rail toggle
    '.skip-link',                                // skip-to-content (a11y; just navigates)

    // --- Snooze / dismiss chips visible only when an alarm is active.
    //    Zero candidates when stack is quiet; clicking re-arms / un-
    //    snoozes which is safe-to-repeat.
    '.snooze-chip',
    '.snooze-chip-close',
];

// Title kept static so Playwright's worker process can re-attach to the
// test between retries. Seed is still deterministic via FUZZ_SEED, logged
// below so a caught failure can be reproduced.
test('fuzz map UI', async ({ page }) => {
    // The per-click budget is bounded: locator timeout 2000 ms + an
    // 80-200 ms post-click settle. Real runs land near 0.4 s per click
    // since most selectors have 1-N candidates and click well under
    // the timeout. Scale the test cap from FUZZ_CLICKS so a heavy
    // bug-hunt run (FUZZ_CLICKS=2400, ~10 min wall-clock) doesn't
    // hit the default 60 s; default of 200 clicks lands at the same
    // 3 min ceiling we used to hard-code.
    test.setTimeout(Math.max(3 * 60 * 1000, FUZZ_CLICKS * 800));
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
