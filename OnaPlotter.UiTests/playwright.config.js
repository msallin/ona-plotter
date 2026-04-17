// Playwright config for OnaPlotter UI tests.
// Defaults to the deployed openplotter.local instance. Override the base URL
// with the BASE_URL env var, e.g.:
//   BASE_URL=http://localhost:5296 npm test
//
// The tests accept a self-signed cert on the default https://openplotter.local
// deployment (ignoreHTTPSErrors below).

import { defineConfig, devices } from '@playwright/test';
import { fileURLToPath } from 'node:url';
import { dirname } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));

// OnaPlotter is served as a SignalK webapp under /signalk-onaplotter/ by
// default. Override with BASE_URL=http://localhost:5296 for a local dev
// build, or with the full webapp URL if the webapp name differs.
// Trailing slash matters: relative paths in tests resolve against this.
const BASE_URL = process.env.BASE_URL ?? 'https://openplotter.local/signalk-onaplotter/';

export default defineConfig({
    testDir: __dirname,
    testMatch: '**/*.spec.js',
    testIgnore: ['**/node_modules/**'],
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
