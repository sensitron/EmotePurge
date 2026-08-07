import { expect, test } from '@playwright/test';

import {
  AUTH_USER,
  MockEmoteUsage,
  installLiveStub,
  mockActiveEmoteSet,
  mockAuthMe,
  mockChannelPermissions,
  mockChannelStatus,
  mockUsageChannelSeries,
  mockUsageDaily,
  mockUsageTotals,
  mockWorkerHealth,
} from './support/mocks';

// The single emote the atlas needs to render a cell at data-atlas-index="0" — nothing here asserts
// on usage numbers, so one row is enough.
const TOUCH_EMOTE: MockEmoteUsage = {
  emoteId: 'e1',
  emoteName: 'PogU',
  sevenTvEmoteId: '7tv-1',
  imageUrl: 'https://cdn.7tv.app/emote/stub/1x.webp',
  totalUseCount: 23,
};

// The contract this file exists for: no 7TV write access without a mouse. The token can only be read
// out of DevTools' local-storage view on 7tv.app, which a phone does not have — so selection, mass
// delete and protocol re-import are desktop work, and the tap on a cell means one thing.
test.describe('touch: reading and voting only', () => {
  test.beforeEach(async ({ page }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page, 'connected');
    await installLiveStub(page);
    await mockChannelPermissions(page, 'sensitron');
    await mockChannelStatus(page, 'sensitron');
    await mockActiveEmoteSet(page, 'sensitron');
    await mockUsageChannelSeries(page, 'sensitron', {});
    await mockUsageDaily(page, 'sensitron', [
      { date: '2026-07-02', useCount: 4 },
      { date: '2026-07-05', useCount: 19 },
    ]);
  });

  test('tapping a cell opens the drilldown instead of selecting it', async ({ page }) => {
    await mockUsageTotals(page, 'sensitron', [TOUCH_EMOTE]);
    await page.goto('/channels/sensitron/usage-stats');

    const cell = page.locator('[data-atlas-index="0"]');
    await expect(cell).toBeVisible();
    // The tell that the cell is no longer a toggle: on a mouse it carries aria-pressed.
    await expect(cell).not.toHaveAttribute('aria-pressed', /.*/);

    await cell.tap();

    await expect(page.locator('#app-dialog-title')).toBeVisible();
  });

  test('the restore panel never appears, and marking cannot happen to raise the dock either', async ({
    page,
  }) => {
    await mockUsageTotals(page, 'sensitron', [TOUCH_EMOTE]);
    await page.goto('/channels/sensitron/usage-stats');
    await page.locator('[data-atlas-index="0"]').tap();
    await expect(page.locator('#app-dialog-title')).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(page.locator('#app-dialog-title')).toHaveCount(0);

    // The restore panel's only gate is `!isCoarse()` (usage-stats-page.html) — nothing else stands
    // between it and rendering, so this assertion is load-bearing: drop that guard and "Protokoll
    // importieren" appears on a plain page load, no tap required.
    await expect(page.getByRole('button', { name: /Protokoll/ })).toHaveCount(0);
    // The dock's guard is `dockVisible() && !isCoarse()`, and dockVisible() needs a selection that a
    // coarse tap can never produce (onCellClick's early return, covered by the first test above) —
    // so on its own this assertion cannot fail from that guard being dropped, only from the two-fault
    // case the first test already rules out. Kept as belt-and-braces documentation of the contract,
    // not as this case's proof.
    await expect(page.getByRole('button', { name: /Löschen/ })).toHaveCount(0);
  });

  test('the drilldown arrives as a sheet docked to the bottom edge', async ({ page }) => {
    await mockUsageTotals(page, 'sensitron', [TOUCH_EMOTE]);
    await page.goto('/channels/sensitron/usage-stats');
    await page.locator('[data-atlas-index="0"]').tap();
    await expect(page.locator('#app-dialog-title')).toBeVisible();

    // Pins the DialogShell/SheetDrag coupling: DialogShell renders the handle only because
    // isSheet() is true, and SheetDrag's onPointerDown reads the literal attribute name to decide
    // whether a drag may start. Renaming it on either side leaves this the only assertion in the
    // suite that would catch it — the geometry checks below come from a `styles.css` media query and
    // do not depend on the handle existing at all.
    await expect(page.locator('.app-dialog-panel [data-sheet-handle]')).toHaveCount(1);

    const pane = page.locator('.cdk-overlay-pane.app-dialog-panel');
    const paneBox = (await pane.boundingBox())!;
    const viewport = page.viewportSize()!;

    // Flush with the bottom, full width — the centred card is neither.
    expect(paneBox.y + paneBox.height).toBeGreaterThanOrEqual(viewport.height - 1);
    expect(paneBox.width).toBe(viewport.width);
  });

  test('the sheet closes when the backdrop is tapped', async ({ page }) => {
    await mockUsageTotals(page, 'sensitron', [TOUCH_EMOTE]);
    await page.goto('/channels/sensitron/usage-stats');
    await page.locator('[data-atlas-index="0"]').tap();
    await expect(page.locator('#app-dialog-title')).toBeVisible();

    await page.locator('.app-dialog-backdrop').tap({ position: { x: 10, y: 10 } });

    await expect(page.locator('#app-dialog-title')).toHaveCount(0);
  });
});
