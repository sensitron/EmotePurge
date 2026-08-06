import { Page, expect, test } from '@playwright/test';

import {
  AUTH_USER,
  installLiveStub,
  mockActiveEmoteSet,
  mockAuthMe,
  mockChannelPermissions,
  mockChannelStatus,
  mockDuplicateEmoteNames,
  mockMyChannels,
  mockUsageDaily,
  mockUsageTotals,
  mockWorkerHealth,
} from './support/mocks';

/**
 * The atlas replaced a card grid with a sprite sheet, and with it the interaction model: 900 cells
 * share ONE tab stop that the arrow keys move (roving tabindex), the bands are a real grouping the
 * navigation has to cross correctly, and the action bar only exists while a selection does. None of
 * that is visible to a unit test — the pure parts are covered in atlas-grid.spec.ts, but whether
 * the focus actually lands on the cell the arrow key aimed at only shows in a browser.
 */

/** A set with a clear head, a middle, a tail and a block of never-used emotes. */
const EMOTES = [
  { name: 'catJAM', uses: 900 },
  { name: 'peepoSad', uses: 700 },
  { name: 'monkaW', uses: 240 },
  { name: 'KEKW', uses: 120 },
  { name: 'Pog', uses: 90 },
  { name: 'Sadge', uses: 40 },
  { name: 'Bedge', uses: 12 },
  { name: 'Copium', uses: 0 },
  { name: 'Susge', uses: 0 },
  { name: 'Clueless', uses: 0 },
].map((emote, index) => ({
  emoteId: `e${index + 1}`,
  emoteName: emote.name,
  sevenTvEmoteId: `7tv-${index + 1}`,
  imageUrl: `https://cdn.7tv.app/emote/${index + 1}/2x.webp`,
  totalUseCount: emote.uses,
  lastUsedDate: emote.uses > 0 ? '2026-07-14' : null,
}));

async function openAtlas(page: Page): Promise<void> {
  await mockAuthMe(page, AUTH_USER);
  await mockWorkerHealth(page);
  await installLiveStub(page);
  await mockMyChannels(page, [
    { channelName: 'sensitron', isBroadcaster: true, isTracked: true, isBotActive: true },
  ]);
  await mockChannelPermissions(page, 'sensitron');
  await mockChannelStatus(page, 'sensitron');
  await mockDuplicateEmoteNames(page, 'sensitron');
  await mockActiveEmoteSet(page, 'sensitron', 'set-1', { capacity: 1000, occupiedSlots: 10 });
  await mockUsageTotals(page, 'sensitron', EMOTES);

  await page.goto('/channels/sensitron/usage-stats');
  await expect(page.getByRole('heading', { name: 'Emote-Nutzung' })).toBeVisible();
}

const cell = (page: Page, name: string) =>
  page.getByRole('button', { name: new RegExp(`^${name} ·`) });

test.describe('emote atlas', () => {
  test('groups the set into weight bands derived from its own usage', async ({ page }) => {
    await openAtlas(page);

    // Pareto, not fixed thresholds: catJAM alone is more than half of the 2102 total, and catJAM
    // plus peepoSad is more than 80 % — so exactly one emote is heavy and exactly one is regular.
    await expect(page.getByRole('heading', { name: 'Tragende Emotes' })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Regelmäßig' })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Selten' })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Nie benutzt' })).toBeVisible();
  });

  test('holds exactly one tab stop and moves it with the arrow keys', async ({ page }) => {
    await openAtlas(page);

    // 900 focusable cells would make the keyboard route through the page unusable, which is what
    // the incumbent card grid did.
    await expect(page.locator('cdk-virtual-scroll-viewport button[tabindex="0"]')).toHaveCount(1);

    await cell(page, 'catJAM').focus();
    await page.keyboard.press('ArrowRight');
    // catJAM is alone in the heavy band, so "right" has to leave the band rather than stop.
    await expect(cell(page, 'peepoSad')).toBeFocused();

    // The last cell of the last band. Never-used emotes all sort equal, so the name tiebreaker
    // decides their order — Clueless, Copium, Susge.
    await page.keyboard.press('End');
    await expect(cell(page, 'Susge')).toBeFocused();

    await page.keyboard.press('Home');
    await expect(cell(page, 'catJAM')).toBeFocused();
  });

  test('marks a cell from the keyboard and opens the action bar', async ({ page }) => {
    await openAtlas(page);

    await expect(page.getByRole('button', { name: /^Löschen \(/ })).toHaveCount(0);

    await cell(page, 'monkaW').focus();
    await page.keyboard.press('Space');

    await expect(cell(page, 'monkaW')).toHaveAttribute('aria-pressed', 'true');
    await expect(page.getByRole('button', { name: 'Löschen (1)' })).toBeVisible();
    // The dock states what the selection costs the set, which is the number the decision turns on.
    await expect(page.getByText('9 von 1000 Slots nach dem Löschen')).toBeVisible();
  });

  test('marks the whole never-used band in one go, and only that band', async ({ page }) => {
    await openAtlas(page);

    await page.getByRole('button', { name: 'alle markieren' }).click();

    await expect(page.getByRole('button', { name: 'Löschen (3)' })).toBeVisible();
    await expect(cell(page, 'Copium')).toHaveAttribute('aria-pressed', 'true');
    await expect(cell(page, 'catJAM')).toHaveAttribute('aria-pressed', 'false');
  });

  test('the action bar disappears again once nothing is marked', async ({ page }) => {
    await openAtlas(page);

    await cell(page, 'Bedge').click();
    await expect(page.getByRole('button', { name: 'Löschen (1)' })).toBeVisible();

    await page.getByRole('button', { name: 'Auswahl aufheben' }).click();

    await expect(page.getByRole('button', { name: /^Löschen \(/ })).toHaveCount(0);
  });

  test('the inspector names whatever cell the pointer is on', async ({ page }) => {
    await openAtlas(page);
    const inspector = page.locator('.app-sticky-bar').last();

    // Before any hover it describes the busiest emote — the honest thing to be looking at first.
    await expect(inspector).toContainText('catJAM');

    await cell(page, 'Sadge').hover();

    await expect(inspector).toContainText('Sadge');
    await expect(inspector).toContainText('Selten');
  });

  test('opens one emote history straight from its own cell', async ({ page }) => {
    await mockUsageDaily(page, 'sensitron', [{ date: '2026-07-14', useCount: 40 }]);
    await openAtlas(page);

    // The reason this trigger sits on the cell rather than in the inspector: the inspector follows
    // the pointer, so reaching a button inside it from a cell in the middle of the sheet means
    // crossing other cells, and it repoints under way. Here the travel is zero — which this test
    // reproduces by hovering the cell and clicking without leaving it.
    await cell(page, 'Sadge').hover();
    await page.getByRole('button', { name: 'Details zu Sadge anzeigen' }).click();

    await expect(page.getByRole('dialog')).toContainText('Sadge');
    // And selecting is still what a click on the cell itself does — the trigger must not leak into
    // the selection the delete path reads.
    await expect(page.getByRole('button', { name: /^Löschen \(/ })).toHaveCount(0);
  });
});
