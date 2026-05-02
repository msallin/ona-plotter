// Shared helpers for Playwright UI tests.

/**
 * Installs console-error and page-error listeners that append to an array.
 * Returns the array plus a function to assert it's empty.
 * Ignores the usual noise (favicon 404, WebSocket disconnects during nav).
 */
export function collectErrors(page) {
    const errors = [];
    page.on('pageerror', (e) => errors.push(`pageerror: ${e.message}`));
    page.on('console', (msg) => {
        if (msg.type() !== 'error') return;
        const text = msg.text();
        // Filter expected noise.
        if (text.includes('favicon')) return;
        if (text.includes('websocket') && text.includes('1006')) return;
        // The browser auto-logs every failed HTTP response as a generic
        // console error: "Failed to load resource: the server responded
        // with a status of XXX (YYY)". The C# side handles 4xx gracefully
        // (TrackApi returns null on non-2xx, SafeLoad surfaces a toast),
        // and the error-relay POST to the SignalK plugin's /log endpoint
        // 400s in dev / CI where the plugin isn't loaded -- so these are
        // not crashes the smoke test should care about.
        //
        // Real-world cases we tolerate:
        //   - 404: webapp running at localhost:5282 without a SignalK
        //     server, or v1/applicationData paths the server hasn't
        //     written to yet.
        //   - 400: SignalK history API (/signalk/v2/api/history/values)
        //     when no history provider plugin is installed; or the
        //     error-relay /log endpoint in dev / CI standalone mode.
        //
        // 401/403 stay flagged (auth misconfig is a real issue) and
        // 5xx stays flagged (server crash is a real issue).
        if (text.includes('Failed to load resource')
            && (text.includes('400 (Bad Request)')
                || text.includes('404 (Not Found)'))) return;
        // Transport-level noise: CORS preflight failure, refused
        // connection, generic ERR_FAILED. These fire when running
        // against a dev / CI environment where the SignalK server
        // (and optional Mayara radar port) isn't reachable. Out of
        // scope for a UI fuzz that hunts JS / Blazor bugs.
        if (text.includes('blocked by CORS policy')) return;
        if (text.includes('net::ERR_FAILED')) return;
        if (text.includes('net::ERR_CONNECTION_REFUSED')) return;
        if (text.includes('Failed to load resource: net::ERR')) return;
        // Blazor logs every unhandled exception through its crit logger; it's
        // covered separately by assertBlazorErrorNotVisible.
        if (text.includes('crit:')) return;
        errors.push(`console.error: ${text}`);
    });
    // Blazor renders a fixed-id error UI for unhandled exceptions.
    // Watch for it becoming visible.
    return {
        errors,
        async assertBlazorErrorNotVisible() {
            const banner = page.locator('#blazor-error-ui');
            if (await banner.isVisible()) {
                const text = (await banner.textContent()) ?? '(empty)';
                throw new Error(`Blazor error UI is visible: ${text.trim()}`);
            }
        }
    };
}

/** Deterministic PRNG so fuzz runs are reproducible. Seed with FUZZ_SEED env. */
export function seededRandom(seed) {
    let s = seed >>> 0;
    return () => {
        s = (s * 1664525 + 1013904223) >>> 0;
        return s / 0x100000000;
    };
}

/** Wait for the Blazor app's #app shell to populate and Leaflet to attach.
 * Also dismisses the welcome card + touch-coachmark + any transient load
 * toasts so follow-up clicks aren't intercepted by overlays. The real
 * app persists the "welcome dismissed" flag in localStorage per device
 * so the card only shows once; Playwright gets a fresh browser per test
 * so without a dismiss step every test would hit the overlay. */
export async function waitForMapReady(page) {
    await page.waitForSelector('#app > *:not(:empty)', { timeout: 30_000 });
    // The Map page has a div#mapDiv once initMap() runs; other pages may not.
    await page.waitForTimeout(1500);
    await dismissInitialOverlays(page);
}

/** Dismiss any of the three click-blocking overlays that can appear on
 * a fresh session: welcome card, touch coachmark, load-failure toasts.
 * Silent if none of them are present -- the map page on day-2 of a
 * device has none of these, and the test must work there too.
 *
 * The welcome card surfaces inside Map.razor.OnAfterRenderAsync after
 * a chain of async work (Settings.Initialize, JS module imports, REST
 * seeds). On slow CI it can appear AFTER the first dismiss pass. We
 * therefore poll for it for a few seconds rather than try-once. */
export async function dismissInitialOverlays(page) {
    // Welcome card: first-visit "Got it" button. A backdrop click would
    // ALSO dismiss but we pick the button to avoid accidentally clicking
    // through to a map marker behind it. Poll for up to 5 s so a late-
    // rendering card on a slow runner still gets dismissed; clicks
    // intercepted by the still-pending welcome dialog were the dominant
    // cause of CI e2e flake before this loop.
    const gotIt = page.locator('button.welcome-dismiss');
    const pollDeadline = Date.now() + 5_000;
    while (Date.now() < pollDeadline) {
        if (await gotIt.count() > 0 && await gotIt.first().isVisible()) {
            try {
                await gotIt.first().click({ force: true, timeout: 500 });
                await page.waitForTimeout(150);
                // Verify gone -- card hides after click. If something
                // re-rendered it, loop continues.
                if (await gotIt.count() === 0) break;
                if (!(await gotIt.first().isVisible())) break;
            } catch { /* card disappeared mid-click */ break; }
        }
        // Also bail if the card doesn't appear in the first second of
        // polling -- on day-2 devices and pre-seeded test contexts the
        // welcome dismissed flag is already set so the card never
        // surfaces. No need to wait the full 5 s in that case.
        if (Date.now() > pollDeadline - 4_000
            && await gotIt.count() === 0) break;
        await page.waitForTimeout(200);
    }

    // Touch coachmark: tap anywhere (its @onclick is on the whole div).
    // Same poll shape as the welcome card -- coachmark is gated on the
    // welcome-dismissed flag so it can also appear late.
    const coachmark = page.locator('.touch-coachmark');
    const coachmarkDeadline = Date.now() + 2_000;
    while (Date.now() < coachmarkDeadline) {
        if (await coachmark.count() > 0 && await coachmark.first().isVisible()) {
            try {
                await coachmark.first().click({ force: true, timeout: 500 });
                await page.waitForTimeout(100);
                if (await coachmark.count() === 0) break;
                if (!(await coachmark.first().isVisible())) break;
            } catch { break; }
        }
        if (Date.now() > coachmarkDeadline - 1_500
            && await coachmark.count() === 0) break;
        await page.waitForTimeout(200);
    }

    // Dismiss transient toasts by tapping them. Load-failure toasts like
    // "Couldn't load regions" auto-clear eventually but block clicks in
    // the meantime. Clicking them closes. Skip silently if the stack is
    // empty.
    const toasts = page.locator('.toast-message');
    const toastCount = await toasts.count();
    for (let i = 0; i < toastCount; i++) {
        try { await toasts.nth(i).click({ force: true, timeout: 500 }); }
        catch { /* toast may have auto-dismissed between count and click */ }
    }
    await page.waitForTimeout(100);
}
