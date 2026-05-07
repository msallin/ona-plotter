// High-level action coverage: exercise the common user flows so a
// regression in one of them trips CI before the user hits it. No
// SignalK server required - these test UI-only flows (dialogs,
// panels, navigation) that don't need live data to open/close.

import { test, expect } from '@playwright/test';
import { collectErrors, waitForMapReady } from './helpers.js';

test('right-click on map opens the context menu', async ({ page }) => {
    const { assertBlazorErrorNotVisible } = collectErrors(page);
    await page.goto('map');
    await waitForMapReady(page);

    // Right-click somewhere in the middle of the map.
    const map = page.locator('#mapDiv');
    const box = await map.boundingBox();
    if (!box) throw new Error('map not laid out');
    await map.click({
        button: 'right',
        position: { x: box.width / 2, y: box.height / 2 }
    });

    // The four primary menu items should render.
    await expect(page.locator('.map-context-menu')).toBeVisible({ timeout: 2000 });
    await expect(page.locator('.ctx-menu-item:has-text("Create Waypoint")')).toBeVisible();
    await expect(page.locator('.ctx-menu-item:has-text("Add Note")')).toBeVisible();
    await expect(page.locator('.ctx-menu-item:has-text("Add Region")')).toBeVisible();
    await assertBlazorErrorNotVisible();
});

test('Create Waypoint context-menu item opens the waypoint dialog', async ({ page }) => {
    const { assertBlazorErrorNotVisible } = collectErrors(page);
    await page.goto('map');
    await waitForMapReady(page);

    const map = page.locator('#mapDiv');
    const box = await map.boundingBox();
    if (!box) throw new Error('map not laid out');
    await map.click({
        button: 'right',
        position: { x: box.width / 2, y: box.height / 2 }
    });
    await page.locator('.ctx-menu-item:has-text("Create Waypoint")').click();

    // Scope by the unique aria-label so the selector survives a future
    // dialog refactor (and so it doesn't accidentally match an input
    // from a different open dialog - the three create dialogs all
    // share .waypoint-dialog .note-dialog classes).
    const dialog = page.locator('.waypoint-dialog')
        .filter({ has: page.locator('input[aria-label="Waypoint name"]') });
    await expect(dialog).toBeVisible();
    await expect(dialog.locator('input[aria-label="Waypoint name"]')).toBeVisible();

    // Cancel path: dialog closes, no crash.
    await dialog.locator('.map-btn:has-text("Cancel")').click();
    await expect(dialog).not.toBeVisible();
    await assertBlazorErrorNotVisible();
});

test('Add Note context-menu item opens the note dialog with description field', async ({ page }) => {
    const { assertBlazorErrorNotVisible } = collectErrors(page);
    await page.goto('map');
    await waitForMapReady(page);

    const map = page.locator('#mapDiv');
    const box = await map.boundingBox();
    if (!box) throw new Error('map not laid out');
    await map.click({
        button: 'right',
        position: { x: box.width / 2, y: box.height / 2 }
    });
    await page.locator('.ctx-menu-item:has-text("Add Note")').click();

    // Note dialog has both the title input and the description textarea.
    const dialog = page.locator('.note-dialog');
    await expect(dialog).toBeVisible();
    await expect(dialog.locator('input[aria-label="Note title"]')).toBeVisible();
    await expect(dialog.locator('textarea[aria-label="Note description"]')).toBeVisible();

    await dialog.locator('.map-btn:has-text("Cancel")').click();
    await expect(dialog).not.toBeVisible();
    await assertBlazorErrorNotVisible();
});

test('Add Region dialog shows radius preset chips', async ({ page }) => {
    const { assertBlazorErrorNotVisible } = collectErrors(page);
    await page.goto('map');
    await waitForMapReady(page);

    const map = page.locator('#mapDiv');
    const box = await map.boundingBox();
    if (!box) throw new Error('map not laid out');
    await map.click({
        button: 'right',
        position: { x: box.width / 2, y: box.height / 2 }
    });
    await page.locator('.ctx-menu-item:has-text("Add Region")').click();

    // Scope by the unique aria-label so a class refactor (or a second
    // dialog being open) can't match the wrong markup. The waypoint /
    // note / region dialogs all share .waypoint-dialog .note-dialog,
    // which used to make this test pass against the wrong dialog.
    const dialog = page.locator('.waypoint-dialog')
        .filter({ has: page.locator('input[aria-label="Region title"]') });
    await expect(dialog).toBeVisible();
    // All five preset chips should render.
    for (const label of ['100 m', '250 m', '500 m', '1 nm', '2 nm']) {
        await expect(dialog.locator(`button:has-text("${label}")`)).toBeVisible();
    }

    await dialog.locator('.map-btn:has-text("Cancel")').click();
    await assertBlazorErrorNotVisible();
});

test('settings page persists the depth-alarm threshold across reload', async ({ page }) => {
    const { assertBlazorErrorNotVisible } = collectErrors(page);
    await page.goto('settings');
    await waitForMapReady(page);

    // Scope by aria-label, not "first number input". A new number input
    // anywhere above the alarms block on the page (e.g. a units picker)
    // would otherwise silently shadow this and make the test edit the
    // wrong field.
    const depthInput = page.locator('input[aria-label="Depth alarm threshold in meters"]');
    await depthInput.fill('4.5');
    await depthInput.blur();

    // Settings.OnDepthChanged is an async event handler that awaits
    // AppSettings.SetDepthAlarmThresholdAsync, which writes through to
    // localStorage using invariant-culture F1 format - 4.5 stays "4.5"
    // but 3 is stored as "3.0". The poll parses the string to a number
    // so the test doesn't care about the exact string form.
    const readDepthKv = () => page.evaluate(() =>
        localStorage.getItem('ona.depthAlarmThreshold'));
    await expect.poll(async () => Number.parseFloat(await readDepthKv()),
        { timeout: 5_000 }).toBe(4.5);

    // Reload and re-read.
    await page.reload();
    await waitForMapReady(page);
    const reloaded = await page.locator('input[aria-label="Depth alarm threshold in meters"]').inputValue();
    expect(reloaded).toBe('4.5');

    // Restore default so subsequent test runs start clean. Same poll
    // pattern ensures the restore actually landed before the test
    // exits, so a retry starts from a clean state.
    const restore = page.locator('input[aria-label="Depth alarm threshold in meters"]');
    await restore.fill('3');
    await restore.blur();
    await expect.poll(async () => Number.parseFloat(await readDepthKv()),
        { timeout: 5_000 }).toBe(3);

    await assertBlazorErrorNotVisible();
});

test('keyboard shortcut ? opens and Esc closes the shortcut overlay', async ({ page }) => {
    const { assertBlazorErrorNotVisible } = collectErrors(page);

    // ShowKeyboardHints defaults OFF so '?' is a no-op for new devices.
    // Flip it on via Settings first so the overlay is reachable in CI.
    // Matches what a desktop-keyboard user would do once in a session.
    await page.goto('settings');
    await waitForMapReady(page);

    // Find the switch by its associated label text; use a real click()
    // (not check({ force: true }), which can skip the @onchange dispatch
    // Blazor needs to flip its in-memory copy + persist to KV).
    const byLabel = page.locator('label.form-check')
        .filter({ hasText: 'keyboard shortcut hints' })
        .locator('input[type="checkbox"]');
    await expect(byLabel).toBeAttached({ timeout: 5_000 });
    if (!(await byLabel.isChecked())) {
        await byLabel.click();
        // Wait for the persisted value to land before navigating away;
        // Blazor's @onchange is async and a same-frame goto can race.
        await expect.poll(
            async () => await page.evaluate(
                () => window.localStorage.getItem('ona.showKeyboardHints.v1')),
            { timeout: 3_000 }).toBe('true');
    }

    await page.goto('map');
    await waitForMapReady(page);
    // Wait for the JS keyboard listener to actually attach.
    // Map.razor.OnAfterRenderAsync calls enableKeyboardShortcuts AFTER
    // initMap + many other async setup steps, so '.leaflet-container'
    // is in the DOM well before the listener exists. The handler
    // stamps html[data-ona-key-shortcuts="on"] when it attaches; wait
    // for that as the deterministic ready signal.
    await page.waitForSelector('html[data-ona-key-shortcuts="on"]',
        { timeout: 10_000 });

    await page.keyboard.press('Shift+Slash');      // '?' on US layouts
    await expect(page.locator('.shortcuts-overlay')).toBeVisible({ timeout: 5_000 });

    await page.keyboard.press('Escape');
    await expect(page.locator('.shortcuts-overlay')).not.toBeVisible();
    await assertBlazorErrorNotVisible();
});
