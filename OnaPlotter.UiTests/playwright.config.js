// Playwright config for OnaPlotter UI tests.
// Defaults to the deployed openplotter.local instance. Override the base URL
// with the BASE_URL env var, e.g.:
//   BASE_URL=http://localhost:5296 npm test
//
// The tests accept a self-signed cert on the default https://openplotter.local
// deployment (ignoreHTTPSErrors below).

import { defineConfig, devices } from '@playwright/test';

const BASE_URL = process.env.BASE_URL ?? 'https://openplotter.local';

export default defineConfig({
    testDir: '.',
    timeout: 60_000,
    expect: { timeout: 10_000 },
    fullyParallel: false, // one browser, one app - keep deterministic
    forbidOnly: !!process.env.CI,
    retries: 0,
    reporter: [['list']],
    use: {
        baseURL: BASE_URL,
        trace: 'retain-on-failure',
        screenshot: 'only-on-failure',
        video: 'retain-on-failure',
        ignoreHTTPSErrors: true,
    },
    projects: [
        {
            name: 'chromium',
            use: { ...devices['Desktop Chrome'] },
        },
    ],
});
