// High-level action coverage: exercise the common user flows so a
// regression in one of them trips CI before the user hits it. No
// SignalK server required -- these test UI-only flows (dialogs,
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

    // Dialog with the name input is up.
    await expect(page.locator('.waypoint-dialog')).toBeVisible();
    await expect(page.locator('.route-name-input').first()).toBeVisible();

    // Cancel path: dialog closes, no crash.
    await page.locator('.map-btn:has-text("Cancel")').click();
    await expect(page.locator('.waypoint-dialog')).not.toBeVisible();
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

    const dialog = page.locator('.note-dialog');
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

    // Find the depth input (first number input in the alarms block).
    const depthInput = page.locator('input[type="number"]').first();
    await depthInput.fill('4.5');
    await depthInput.blur();

    // Reload and re-read.
    await page.reload();
    await waitForMapReady(page);
    const reloaded = await page.locator('input[type="number"]').first().inputValue();
    expect(reloaded).toBe('4.5');

    // Restore default so subsequent test runs start clean.
    const restore = page.locator('input[type="number"]').first();
    await restore.fill('3');
    await restore.blur();

    await assertBlazorErrorNotVisible();
});

test('keyboard shortcut ? opens and Esc closes the shortcut overlay', async ({ page }) => {
    const { assertBlazorErrorNotVisible } = collectErrors(page);
    await page.goto('map');
    await waitForMapReady(page);

    await page.keyboard.press('Shift+Slash');      // '?' on US layouts
    await expect(page.locator('.shortcuts-overlay')).toBeVisible({ timeout: 2000 });

    await page.keyboard.press('Escape');
    await expect(page.locator('.shortcuts-overlay')).not.toBeVisible();
    await assertBlazorErrorNotVisible();
});
