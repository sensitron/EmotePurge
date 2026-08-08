import { Page, expect, test } from '@playwright/test';

import {
  AUTH_USER,
  MockEmoteUsage,
  installLiveStub,
  mockActiveEmoteSet,
  mockAuthMe,
  mockChannelPermissions,
  mockChannelStatus,
  mockDuplicateEmoteNames,
  mockMyChannels,
  mockUsageChannelSeries,
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

async function openAtlas(page: Page, emotes: MockEmoteUsage[] = EMOTES): Promise<void> {
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
  await mockUsageTotals(page, 'sensitron', emotes);

  await page.goto('/channels/sensitron/usage-stats');
  await expect(page.getByRole('heading', { name: 'Emote-Nutzung' })).toBeVisible();
  // The heading sits outside the loading branch, so it proves nothing about the sheet. Wait for the
  // skeleton to go: it is a second role="status" (§6.1 gives every skeleton one), and while it is up
  // a bare getByRole('status') resolves to two elements and fails on strict mode rather than on the
  // thing under test.
  await expect(page.getByRole('status', { name: 'Lädt…' })).toHaveCount(0);
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

  test('space marks a cell, enter opens its history', async ({ page }) => {
    await mockUsageDaily(page, 'sensitron', [{ date: '2026-07-14', useCount: 120 }]);
    await openAtlas(page);

    // Both used to do the same thing, because both fire a native click on a button. Splitting them
    // is what gives the keyboard a route to the per-cell trigger without a second tab stop.
    await cell(page, 'KEKW').focus();
    await page.keyboard.press('Enter');

    await expect(page.getByRole('dialog')).toContainText('KEKW');
    // Closed first on purpose: the CDK dialog puts aria-hidden on everything behind it, so any
    // role-based assertion about the grid would pass while the dialog is up whatever the truth is.
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog')).toHaveCount(0);
    await expect(cell(page, 'KEKW')).toHaveAttribute('aria-pressed', 'false');
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

  test('the sidecar names whatever cell the pointer is on', async ({ page }) => {
    await openAtlas(page);
    const sidecar = page.getByRole('complementary');

    // Before any hover it describes the busiest emote — the honest thing to be looking at first.
    await expect(sidecar).toContainText('catJAM');

    await cell(page, 'Sadge').hover();

    await expect(sidecar).toContainText('Sadge');
    await expect(sidecar).toContainText('Selten');
  });

  test('the curve states its scale, and the green bands say what the emote did on them', async ({
    page,
  }) => {
    // catJAM's 900 uses, as a curve peaking at 700. Distinct from every other number the sidecar
    // prints, so an exact-text match can tell the axis apart from the totals below it.
    // Live offsets counted from the mocked tracking start 2026-06-12: the 15., 16., 17. and 21.
    // The curve puts 700 on the 15. and nothing on the other three.
    await mockUsageChannelSeries(
      page,
      'sensitron',
      {
        e1: [
          [1, 200],
          [3, 700],
        ],
      },
      [3, 4, 5, 9],
    );
    await openAtlas(page);
    const sidecar = page.getByRole('complementary');

    // The axis is aria-hidden — the peak line carries the same maximum in words, which is what
    // keeps the graphic from meaning anything on its own. Asserted on the rendered text all the
    // same, because a scale nobody can read is the thing this exists to prevent.
    await expect(sidecar.getByText('700', { exact: true })).toBeVisible();
    await expect(sidecar.getByText('0', { exact: true })).toBeVisible();
    // The emote-specific statement, not the channel-wide one that used to stand here and read the
    // same for every emote. Both numbers are pinned: they follow the offsets above, not the calendar.
    await expect(sidecar).toContainText('An 3 von 4 Live-Tagen nicht benutzt');
    await expect(sidecar).not.toContainText('Live an');
  });

  test('the channel-wide live count is stated once, above the sheet', async ({ page }) => {
    // It answers a question about the stream, not about any one emote, so it belongs to the page.
    await mockUsageChannelSeries(page, 'sensitron', { e1: [[3, 700]] }, [3, 4, 5, 9]);
    await openAtlas(page);

    await expect(
      page.getByText(/Im gewählten Zeitraum war der Stream an 4 Tagen live\./),
    ).toBeVisible();
  });

  test('says nothing about live days for a range with no coverage', async ({ page }) => {
    // "0 of 57 days" would report an absence we never measured: a range older than the live poll has
    // no coverage data at all, which is not the same as a channel that never went live.
    await mockUsageChannelSeries(page, 'sensitron', {
      e1: [
        [1, 200],
        [3, 700],
      ],
    });
    await openAtlas(page);
    const sidecar = page.getByRole('complementary');

    await expect(sidecar.getByText('700', { exact: true })).toBeVisible();
    await expect(sidecar).not.toContainText('Live-Tag');
    await expect(page.getByText(/war der Stream an/)).toHaveCount(0);
  });

  test('draws no line for the days before the emote entered the set', async ({ page }) => {
    // The emote joined the set on the 20., eight days into the range. A baseline over the days
    // before that reads as "unused" where it should read as "did not exist" — the whole point.
    await mockUsageChannelSeries(page, 'sensitron', { e1: [[3, 700]] }, [3, 4, 5, 9]);
    await openAtlas(
      page,
      EMOTES.map((emote) =>
        emote.emoteId === 'e1' ? { ...emote, firstSeenAt: '2026-06-20T00:00:00Z' } : emote,
      ),
    );
    const sidecar = page.getByRole('complementary');

    const points = await sidecar.locator('polyline').getAttribute('points');
    const firstX = Number(points!.split(' ')[0].split(',')[0]);
    expect(firstX).toBeGreaterThan(0);

    // Only the 21. falls inside the emote's lifetime, and the curve has nothing on it. Singular,
    // because "An 1 von 1 Live-Tagen" is not a sentence.
    await expect(sidecar).toContainText('Am einzigen Live-Tag nicht benutzt');
  });

  test('below the sidecar breakpoint the same readout is a line, and only one of them shows', async ({
    page,
  }) => {
    // 16rem of panel is a third of a narrow window, so under lg the readout collapses back into a
    // row of the toolbar. Both existing at once would say the same thing twice.
    await page.setViewportSize({ width: 900, height: 900 });
    await openAtlas(page);

    await expect(page.getByRole('complementary')).toBeHidden();
    await expect(page.locator('.app-sticky-bar').last()).toContainText('catJAM');
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
    // Closed first: the CDK dialog hides everything behind it from the accessibility tree, so this
    // assertion would pass vacuously with the dialog still up.
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog')).toHaveCount(0);
    // The trigger must not leak into the selection the delete path reads.
    await expect(cell(page, 'Sadge')).toHaveAttribute('aria-pressed', 'false');
    await expect(page.getByRole('button', { name: /^Löschen \(/ })).toHaveCount(0);
  });

  // The usage filter used to be three controls — Min, Max, and an "unused only" toggle that was not
  // a filter of its own but *was* min = 0 / max = 0, overwriting the two fields beside it. As one
  // menu the states have to stay reachable and, unlike the old toggle, re-picking the selected one
  // must not switch it back off.
  test('the usage menu narrows the sheet to the never-used emotes and says so on its trigger', async ({
    page,
  }) => {
    await openAtlas(page);
    await expect(page.getByRole('status')).toContainText('10 von 10');

    await page.getByRole('button', { name: /^Nutzung:/ }).click();
    await page.getByRole('radio', { name: 'nie benutzt' }).click();

    await expect(page.getByRole('button', { name: 'Nutzung: nie benutzt' })).toBeVisible();
    await expect(page.getByRole('status')).toContainText('3 von 10');
    await expect(cell(page, 'catJAM')).toHaveCount(0);
    await expect(cell(page, 'Copium')).toBeVisible();

    // The way back, which before this only existed once a filter had emptied the sheet entirely.
    await page.getByRole('button', { name: 'Filter zurücksetzen' }).click();
    await expect(page.getByRole('button', { name: 'Nutzung: alle' })).toBeVisible();
    await expect(page.getByRole('status')).toContainText('10 von 10');
  });

  test('a custom bound survives reopening the menu and states itself on the trigger', async ({
    page,
  }) => {
    await openAtlas(page);

    await page.getByRole('button', { name: /^Nutzung:/ }).click();
    await page.getByRole('radio', { name: 'eigener Bereich' }).click();
    await page.getByLabel('Höchstens').fill('100');
    await page.getByRole('button', { name: 'Fertig' }).click();

    await expect(page.getByRole('button', { name: 'Nutzung: bis 100×' })).toBeVisible();
    await expect(cell(page, 'catJAM')).toHaveCount(0);
    await expect(cell(page, 'Pog')).toBeVisible();

    // Reopening must land back on "custom" with the value still in the field: the preset cannot be
    // derived from the bounds alone, and an emptied bound would otherwise read as "all".
    await page.getByRole('button', { name: /^Nutzung:/ }).click();
    await expect(page.getByRole('radio', { name: 'eigener Bereich' })).toHaveAttribute(
      'aria-checked',
      'true',
    );
    await expect(page.getByLabel('Höchstens')).toHaveValue('100');
  });

  test('the drilldown curve keeps quiet about the days before the emote existed', async ({
    page,
  }) => {
    // Same statement as in the sidecar, from the other data path: the dialog loads its own per-emote
    // series with ISO live days, while the sidecar reads the batch response's offsets.
    await mockUsageDaily(
      page,
      'sensitron',
      [{ date: '2026-06-21', useCount: 40 }],
      ['2026-06-15', '2026-06-16', '2026-06-21'],
    );
    await openAtlas(
      page,
      EMOTES.map((emote) =>
        emote.emoteName === 'Sadge' ? { ...emote, firstSeenAt: '2026-06-20T00:00:00Z' } : emote,
      ),
    );

    await cell(page, 'Sadge').hover();
    await page.getByRole('button', { name: 'Details zu Sadge anzeigen' }).click();
    const dialog = page.getByRole('dialog');
    await expect(dialog).toContainText('Sadge');

    const points = await dialog.locator('polyline').getAttribute('points');
    const firstX = Number(points!.split(' ')[0].split(',')[0]);
    expect(firstX).toBeGreaterThan(0);

    // Only the 21. falls inside the emote's lifetime, and it was used that day — so the positive
    // form, not "0 unused".
    await expect(dialog).toContainText('Am einzigen Live-Tag benutzt');
    await expect(dialog).not.toContainText('Live an');

    // Closed first: the CDK dialog hides everything behind it from the accessibility tree.
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog')).toHaveCount(0);
  });
});
