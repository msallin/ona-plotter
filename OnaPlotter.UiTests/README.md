# OnaPlotter UI Tests

Playwright smoke + fuzz tests. Runs against a live deployment by default
(the SignalK webapp at `https://openplotter.local`), override with
`BASE_URL=http://localhost:5282`.

## One-time setup

```sh
cd OnaPlotter.UiTests
npm install
npm run install:deps    # downloads a chromium build
```

## Run

```sh
# Smoke: every page loads, every control button clicks, no crash banner.
npm run test:smoke

# Fuzz: 40 random clicks on the map page with a seeded PRNG. Re-run a
# specific seed to reproduce a failure:
FUZZ_SEED=12345 npm run test:fuzz

# Point at a locally-running dev build instead of the deployed instance:
BASE_URL=http://localhost:5282 npm test
```

## What's tested

- **smoke.spec.js**: walks every top-level route, exercises all map control
  buttons, opens/closes the layers panel. Fails if the Blazor error banner
  (`#blazor-error-ui`) ever becomes visible, or the page raises errors.
- **fuzz.spec.js**: a seeded-PRNG walk over safe interactive selectors
  (`.ctrl-btn`, `.chart-quick-chip`, `.vessel-row`, etc.). Deliberately
  avoids destructive actions like starting route edit or activating MOB.

Traces, screenshots, and video are kept for failed runs under
`test-results/`.

## Adding a test

Keep selectors stable: if a fuzz test hits a previously unsafe click path,
reproduce it locally with the logged `FUZZ_SEED`, fix the underlying crash
in C#/JS, then add the specific sequence as a new named test in
`smoke.spec.js` so it becomes a regression case.
