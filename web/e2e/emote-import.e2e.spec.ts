import { Locator, Page, expect, test } from '@playwright/test';

import {
  AUTH_USER,
  MockEmoteUsage,
  emitLive,
  installLiveStub,
  mockActiveEmoteSet,
  mockAuthMe,
  mockChannelPermissions,
  mockChannelScopedResync,
  mockChannelStatus,
  mockDuplicateEmoteNames,
  mockEmoteList,
  mockMyChannels,
  mockSetWarning,
  mockSevenTvGql,
  mockSyncImported,
  mockUsageTotals,
  mockVoteSessionList,
  mockWorkerHealth,
} from './support/mocks';

/**
 * The push flow (#72, K3): pick emotes on the usage-stats grid, choose a destination (another
 * channel or a file), and — for a channel destination — see the confirmation dialog before
 * anything is written. R14: no isolated component tests for these dialogs (Regel 12), so this is
 * their only coverage besides the audit-harness screenshots (`ui-audit.audit.ts`).
 */

const SOURCE_CHANNEL = 'sensitron';
const TARGET_CHANNEL = 'aatrociity';

const SOURCE_EMOTES: MockEmoteUsage[] = [
  {
    emoteId: 'e1',
    emoteName: 'CatJAM',
    sevenTvEmoteId: '7tv-1',
    imageUrl: 'https://cdn.7tv.app/emote/1/2x.webp',
    totalUseCount: 500,
  },
  {
    emoteId: 'e2',
    emoteName: 'KEKW',
    sevenTvEmoteId: '7tv-2',
    imageUrl: 'https://cdn.7tv.app/emote/2/2x.webp',
    totalUseCount: 300,
  },
  {
    emoteId: 'e3',
    emoteName: 'Pog',
    sevenTvEmoteId: '7tv-3',
    imageUrl: 'https://cdn.7tv.app/emote/3/2x.webp',
    totalUseCount: 50,
  },
];

/** The target channel's own grid, deliberately sharing no emote id or name with SOURCE_EMOTES: on
 *  the target page a visible `Sadge` cell is proof the rows really changed hands. */
const TARGET_EMOTES: MockEmoteUsage[] = [
  {
    emoteId: 't1',
    emoteName: 'Sadge',
    sevenTvEmoteId: '7tv-t1',
    imageUrl: 'https://cdn.7tv.app/emote/11/2x.webp',
    totalUseCount: 120,
  },
];

/**
 * Holds an already-routed endpoint until the returned callback is invoked, then lets the handler
 * registered *before* it answer normally (`route.fallback`). Playwright matches handlers in reverse
 * registration order, so this must be registered after the mock it defers to.
 *
 * Used to pin the ordering of two responses that normally race, which is the only way to observe a
 * page state that exists between them.
 */
async function deferRoute(page: Page, pattern: string): Promise<() => void> {
  let release!: () => void;
  const held = new Promise<void>((resolve) => {
    release = resolve;
  });
  await page.route(pattern, async (route) => {
    await held;
    await route.fallback();
  });
  return release;
}

/** One channel's usage-stats page, mocked enough to load — permissions, status, the duplicate-name
 *  check and the totals grid. Mirrors `usage-atlas.e2e.spec.ts`'s `openAtlas` helper, generalized
 *  to a channel name so both the source and (in the channel-switch checks) the target page can use
 *  it. */
async function mockWorkspace(
  page: Page,
  channelName: string,
  emotes: MockEmoteUsage[] = [],
  activeEmoteSetId = 'set-1',
): Promise<void> {
  await mockChannelPermissions(page, channelName);
  await mockChannelStatus(page, channelName);
  await mockDuplicateEmoteNames(page, channelName);
  await mockActiveEmoteSet(page, channelName, activeEmoteSetId, {
    capacity: 1000,
    occupiedSlots: 3,
  });
  await mockUsageTotals(page, channelName, emotes);
}

async function gotoUsageStats(page: Page, channelName: string): Promise<void> {
  await page.goto(`/channels/${channelName}/usage-stats`);
  await expect(page.getByRole('heading', { name: 'Emote-Nutzung' })).toBeVisible();
  // Same reasoning as usage-atlas's openAtlas: wait out the skeleton's own role="status" so a bare
  // getByRole('status') later does not resolve to two elements under strict mode.
  await expect(page.getByRole('status', { name: 'Lädt…' })).toHaveCount(0);
}

const cell = (page: Page, name: string) =>
  page.getByRole('button', { name: new RegExp(`^${name} ·`) });

// Exact match, not the Playwright default (substring): the dock's shortcut (#80, §8.7) carries the
// same verb plus a trailing count — "Übertragen… (2)" — which would otherwise also match
// this name and turn every use below into a strict-mode violation. This locator is always the
// HEADER trigger; see dockCopyButton for the dock's second entry point into openImportTarget().
const copyButton = (page: Page) => page.getByRole('button', { name: 'Übertragen…', exact: true });

// The dock's shortcut into openImportTarget('selection') (#80, §8.7): same verb as copyButton, no
// scope radiogroup in the dialog it opens, count baked into the accessible name.
const dockCopyButton = (page: Page, count: number) =>
  page.getByRole('button', { name: `Übertragen… (${count})`, exact: true });

/**
 * Opens the file-import dialog (#91) via the header trigger and returns the file input sitting
 * inside it. Locale-independent by position, same reasoning as `ui-audit.audit.ts:801-806` for its
 * neighbour: the trigger's label is translated and shares no word with the other header buttons, so
 * this goes by position instead — `main header button` `.nth(2)`, after `.nth(0)` (Exportieren) and
 * `.nth(1)` (Übertragen…). Scoped to `main` because the app shell has its own top-level `<header>`
 * (the account menu) that an unscoped `header button` would count first.
 */
async function openFileImportDialog(page: Page): Promise<Locator> {
  const dialog = page.getByRole('dialog');
  await page.locator('main header button').nth(2).click();
  await expect(dialog.locator('#app-dialog-title')).toHaveText('Datei einspielen');
  return dialog.locator('input[type="file"]');
}

/**
 * Waits past the still-open file-import dialog (plan §1.1, task-5 "Falle 1"): after
 * `setInputFiles`, that dialog stays open until `file.text()` resolves, only then closing and
 * handing off to the confirm dialog. A bare wait on `#app-dialog-title` resolves immediately
 * against the file-import dialog's OWN title (still attached at that instant) rather than waiting
 * for the confirm dialog to replace it — the following click on "Abbrechen" would then land on the
 * wrong dialog, and the failure would look like a timing flake. `toHaveText` instead polls until the
 * title reads as the import-confirm dialog's own ("N Emote(s) nach <channel> kopieren?"), so it
 * survives the transition between the two dialogs.
 */
async function waitForImportConfirmDialog(page: Page): Promise<Locator> {
  const dialog = page.getByRole('dialog');
  await expect(dialog.locator('#app-dialog-title')).toHaveText(/kopieren\?$/);
  return dialog;
}

test.describe('push flow: picker to confirmation dialog', () => {
  test('a channel target shows origin, target, an already-present row and a name collision', async ({
    page,
  }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    // Broadcaster of the current channel; an editor of one tracked and one untracked channel; a
    // moderator-only channel — the filter is isBroadcaster || isSevenTvEditor (import-target-
    // options.ts), so modonly must not appear and the current channel is always excluded.
    await mockMyChannels(page, [
      { channelName: SOURCE_CHANNEL, isBroadcaster: true, isTracked: true },
      { channelName: TARGET_CHANNEL, isSevenTvEditor: true, isTracked: true },
      { channelName: 'untrackedbuddy', isSevenTvEditor: true, isTracked: false },
      { channelName: 'modonly', isModerator: true, isTracked: true },
    ]);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);
    // Target's own data, fetched by loadImportTarget once a target is chosen: one row shares an id
    // with a selected emote (CatJAM, 7tv-1 — already present), the other shares a NAME with a
    // selected emote under a different id (KEKW's name, different id — a collision).
    await mockActiveEmoteSet(page, TARGET_CHANNEL, 'target-set', {
      capacity: 1000,
      occupiedSlots: 3,
    });
    await mockSetWarning(page, TARGET_CHANNEL);
    await mockEmoteList(page, TARGET_CHANNEL, [
      { sevenTvEmoteId: '7tv-1', name: 'CatJAM' },
      { sevenTvEmoteId: 'target-99', name: 'KEKW' },
    ]);

    await gotoUsageStats(page, SOURCE_CHANNEL);

    await cell(page, 'CatJAM').click();
    await cell(page, 'KEKW').click({ modifiers: ['Shift'] });
    await expect(copyButton(page)).toBeEnabled();
    await copyButton(page).click();

    const picker = page.getByRole('dialog');
    await expect(picker.locator('#app-dialog-title')).toHaveText('Emotes übertragen');

    // Scope defaults to the selection (R12), not to the visible list.
    await expect(picker.getByRole('radio', { name: 'Auswahl (2)' })).toBeChecked();

    // Exactly the three expected rows, in alphabetical order, and nothing else.
    await expect(picker.getByRole('radio', { name: '#aatrociity' })).toBeEnabled();
    await expect(
      picker.getByRole('radio', { name: /^#untrackedbuddy \(Kanal muss erst beitreten\)$/ }),
    ).toBeDisabled();
    await expect(picker.getByRole('radio', { name: /als Datei speichern/ })).toBeVisible();
    await expect(picker.getByText('#modonly')).toHaveCount(0);
    await expect(picker.getByText('#sensitron', { exact: true })).toHaveCount(0);

    await picker.getByRole('radio', { name: '#aatrociity' }).check();
    await picker.getByRole('button', { name: 'Weiter' }).click();

    const confirm = page.getByRole('dialog');
    await expect(confirm.locator('#app-dialog-title')).toHaveText(
      '1 Emote nach aatrociity kopieren?',
    );
    await expect(confirm.getByText('Aus Kanal sensitron')).toBeVisible();
    await expect(confirm.getByText('Ziel: aatrociity · Set target-set')).toBeVisible();
    await expect(
      confirm.getByText('1 Emote ist bereits im Zielset und wird übersprungen.'),
    ).toBeVisible();
    await expect(
      confirm.getByText('1 Name ist im Zielset schon vergeben — 7TV wird dieses Emote ablehnen:'),
    ).toBeVisible();
    await expect(confirm.locator('app-name-preview-list')).toContainText('KEKW');
    // occupied 3 + the one row that survives the already-present filter (KEKW, name collision but
    // still added — nameCollisions stays IN toAdd per the import-preview contract) = 4 of 1000.
    await expect(confirm.getByText('Das Set hätte danach 4 von 1000 Slots belegt.')).toBeVisible();
    await expect(confirm.getByRole('button', { name: 'Kopieren' })).toBeEnabled();
  });

  /**
   * The dock's second entry point into the same flow (#80, §8.7): same verb, but `forcedScope:
   * 'selection'` skips the scope question outright instead of merely defaulting to it. Proven two
   * ways rather than just reading `forcedScope` off the component: the radiogroup the header path
   * shows in the test above is entirely absent here despite a selection existing (the condition
   * that would normally render it), and the run that follows touches only the two MARKED rows —
   * Pog stays out of both the confirmation count and the 7TV calls, even though it is visible on
   * the same grid and would have been included under scope `visible`.
   */
  test('the dock shortcut skips the scope question and copies exactly the marked rows', async ({
    page,
  }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockMyChannels(page, [
      { channelName: SOURCE_CHANNEL, isBroadcaster: true, isTracked: true },
      { channelName: TARGET_CHANNEL, isSevenTvEditor: true, isTracked: true },
    ]);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);
    await mockActiveEmoteSet(page, TARGET_CHANNEL, 'target-set', {
      capacity: 1000,
      occupiedSlots: 3,
    });
    await mockSetWarning(page, TARGET_CHANNEL);
    // Empty target set: nothing to collide with, so the row count in the confirm dialog is pure
    // proof of scope, not diluted by an already-present or name-collision filter.
    await mockEmoteList(page, TARGET_CHANNEL, []);
    await mockSyncImported(page, TARGET_CHANNEL);
    await mockChannelScopedResync(page, TARGET_CHANNEL);

    const addedEmoteIds: unknown[] = [];
    await mockSevenTvGql(page, (request) => {
      addedEmoteIds.push(request.variables['emoteId']);
      return { data: { emoteSet: { emotes: [{ id: request.variables['emoteId'] }] } } };
    });
    // Frozen for the same reason as the other run-completion tests: the engine's trailing pacing
    // delay would otherwise race a real wait.
    await page.clock.install();

    await gotoUsageStats(page, SOURCE_CHANNEL);

    // Marks CatJAM and KEKW, deliberately leaving Pog unmarked — the third SOURCE_EMOTES row that
    // scope `visible` would have swept in.
    await cell(page, 'CatJAM').click();
    await cell(page, 'KEKW').click({ modifiers: ['Shift'] });
    await expect(dockCopyButton(page, 2)).toBeEnabled();
    await dockCopyButton(page, 2).click();

    const picker = page.getByRole('dialog');
    await expect(picker.locator('#app-dialog-title')).toHaveText('Emotes übertragen');
    // The scope question itself is gone, not just pre-answered — contrast with the header path's
    // "Auswahl (2)" radio checked by default in the test above.
    await expect(picker.getByRole('radiogroup', { name: 'Exportumfang' })).toHaveCount(0);
    await expect(picker.getByRole('radio', { name: /^Auswahl/ })).toHaveCount(0);
    await expect(picker.getByRole('radio', { name: /^Gefilterte Liste/ })).toHaveCount(0);

    await picker.getByRole('radio', { name: '#aatrociity' }).check();
    await picker.getByRole('button', { name: 'Weiter' }).click();

    const confirm = page.getByRole('dialog');
    // Two, not three: proof the forced scope actually reached the confirm step, not just the
    // picker's own rendering.
    await expect(confirm.locator('#app-dialog-title')).toHaveText(
      '2 Emotes nach aatrociity kopieren?',
    );
    await confirm.getByRole('button', { name: 'Kopieren' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    await page.clock.runFor(2000);
    await expect(page.getByText('2 kopiert · 0 fehlgeschlagen · 0 abgebrochen')).toBeVisible();

    // CatJAM and KEKW's ids, in either order, and nothing else — Pog's 7tv-3 never went out.
    expect(addedEmoteIds.sort()).toEqual(['7tv-1', '7tv-2']);
  });

  /**
   * #80 review fix: a silent totals reload (`usage.flushed`/`channel.synced`, both routed through
   * `loadTotals(..., { preserveSelection: true })`) can drop a marked row out of `atlasOrder()` —
   * an emote archived on 7TV from outside this tab is the concrete cause, since the totals query
   * filters `!e.IsArchived` — without touching `selection.selectedKeys()`, which `preserveSelection`
   * only ever leaves alone. Before this fix the dock shortcut's count and lock followed that raw,
   * now-stale key count: the label kept promising the old row count and the button stayed enabled,
   * so a click ran straight into `openImportTarget`'s `captured.selection.length === 0` guard and
   * did nothing — no dialog, no error, no feedback. This proves both the label and the lock now
   * follow `importShortcutSelectionCount` (built on `selection.selectedItems()`) instead.
   */
  test('a live reload that archives every marked row relocks the dock shortcut instead of running silently into an empty capture', async ({
    page,
  }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockMyChannels(page, [
      { channelName: SOURCE_CHANNEL, isBroadcaster: true, isTracked: true },
    ]);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);
    await page.clock.install();

    await gotoUsageStats(page, SOURCE_CHANNEL);

    // Marks CatJAM and KEKW; Pog is deliberately left both unmarked and, below, the only row the
    // reload still returns — its continued presence is proof the grid really reloaded rather than
    // having gone blank.
    await cell(page, 'CatJAM').click();
    await cell(page, 'KEKW').click({ modifiers: ['Shift'] });
    await expect(dockCopyButton(page, 2)).toBeEnabled();

    // Re-registering the same route wins over mockWorkspace's earlier one (Playwright runs the
    // most-recently registered handler first, and this one fulfills instead of falling back) — the
    // next totals fetch answers as if CatJAM and KEKW had just been archived from outside this tab.
    await mockUsageTotals(page, SOURCE_CHANNEL, [SOURCE_EMOTES[2]]);
    await emitLive(page, { type: 'usage.flushed', channel: SOURCE_CHANNEL });
    // liveReload collapses the burst over CHANNEL_RELOAD_DEBOUNCE_MS (1 s) before it reloads.
    await page.clock.runFor(1_500);

    await expect(cell(page, 'Pog')).toBeVisible();
    await expect(cell(page, 'CatJAM')).toHaveCount(0);

    // The raw selection is untouched by preserveSelection (still 2 keys — see the dock's own "2
    // markiert", not asserted here since it is explicitly out of scope for this fix), but neither
    // marked row resolves against the reloaded grid any more, so the shortcut must show and enforce
    // zero, not the stale two.
    await expect(dockCopyButton(page, 2)).toHaveCount(0);
    await expect(dockCopyButton(page, 0)).toBeDisabled();
  });
});

test.describe('push flow: the file path', () => {
  // Both an emote-list and a usage export lead into the same confirmation dialog, uploaded through
  // the file-import dialog opened from the header trigger (not the picker's "save as file" option)
  // — the dialog dispatches on the envelope's `kind` (FileImportDialog.onFileSelected) and, on
  // success, closes and hands the result to the trigger. The target here is always the CURRENT
  // channel: FileImportTrigger.openDialog always imports into the `channelName` it was opened with.
  test('an emote-list file and a usage-export file both reach the confirm dialog', async ({
    page,
  }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockMyChannels(page, [
      { channelName: SOURCE_CHANNEL, isBroadcaster: true, isTracked: true },
    ]);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);
    await mockSetWarning(page, SOURCE_CHANNEL);
    // Fixed target picture for the whole test: two emotes already present under these exact ids —
    // the first file's rows collide with both, the third file's rows are deliberately fresh ones.
    await mockEmoteList(page, SOURCE_CHANNEL, [
      { sevenTvEmoteId: '7tv-existing-1', name: 'ExistingA' },
      { sevenTvEmoteId: '7tv-existing-2', name: 'ExistingB' },
    ]);

    await gotoUsageStats(page, SOURCE_CHANNEL);

    // File 1: an emote-list export from THIS channel, both rows already in the target — both
    // sameChannelFile and nothingToAdd apply, and the execute button is locked.
    let fileInput = await openFileImportDialog(page);
    await fileInput.setInputFiles({
      name: 'emotepurge_sensitron_emote-list_2026-09-01.json',
      mimeType: 'application/json',
      buffer: Buffer.from(
        JSON.stringify({
          source: 'emotepurge',
          kind: 'emote-list',
          formatVersion: 1,
          exportedAt: '2026-09-01T09:00:00Z',
          channelName: 'sensitron',
          withheld: [],
          meta: { sourceEmoteSetId: 'set-1', rowCount: 2, scope: 'visible' },
          rows: [
            { sevenTvEmoteId: '7tv-existing-1', name: 'ExistingA' },
            { sevenTvEmoteId: '7tv-existing-2', name: 'ExistingB' },
          ],
        }),
        'utf-8',
      ),
    });
    let dialog = await waitForImportConfirmDialog(page);
    await expect(dialog.getByText('Diese Liste stammt aus diesem Kanal.')).toBeVisible();
    await expect(dialog.getByText('Alle 2 Emotes sind bereits im Zielset.')).toBeVisible();
    await expect(dialog.getByRole('button', { name: 'Kopieren' })).toBeDisabled();
    await dialog.getByRole('button', { name: 'Abbrechen' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    // File 2: a usage export with no `exportedAt` at all — the date reads as unknown rather than
    // crashing or silently defaulting to "now".
    fileInput = await openFileImportDialog(page);
    await fileInput.setInputFiles({
      name: 'emotepurge_sensitron_usage_2026-08-01_2026-08-30.json',
      mimeType: 'application/json',
      buffer: Buffer.from(
        JSON.stringify({
          source: 'emotepurge',
          kind: 'usage',
          formatVersion: 1,
          channelName: 'sensitron',
          withheld: [],
          meta: {
            from: '2026-08-01',
            to: '2026-08-30',
            rowCount: 1,
            scope: 'visible',
            filtered: false,
          },
          rows: [
            {
              sevenTvEmoteId: '7tv-new-20',
              emoteName: 'FreshEmote',
              totalUseCount: 5,
              previousWindowUseCount: 0,
              lastUsedDate: null,
              firstSeenAt: null,
              trend: 'unknown',
            },
          ],
        }),
        'utf-8',
      ),
    });
    dialog = await waitForImportConfirmDialog(page);
    await expect(dialog.getByText('Export aus sensitron, Datum unbekannt')).toBeVisible();
    await dialog.getByRole('button', { name: 'Abbrechen' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    // File 3: meta.rowCount claims 5 rows, but the array itself carries 4 — one structurally
    // invalid (no sevenTvEmoteId) and one valid duplicate of another valid row. discardedRows is
    // measured against the claimed count (5 - 3 structurally valid = 2, R6/2.7), independent of
    // duplicatesCollapsed (1, from the dedup that runs after validity filtering) — and the
    // contract (T5) puts the discarded-rows line before the duplicates-collapsed line.
    fileInput = await openFileImportDialog(page);
    await fileInput.setInputFiles({
      name: 'emotepurge_sensitron_emote-list_2026-09-02.json',
      mimeType: 'application/json',
      buffer: Buffer.from(
        JSON.stringify({
          source: 'emotepurge',
          kind: 'emote-list',
          formatVersion: 1,
          exportedAt: '2026-09-02T09:00:00Z',
          channelName: 'sensitron',
          withheld: [],
          meta: { sourceEmoteSetId: 'set-1', rowCount: 5, scope: 'visible' },
          rows: [
            { sevenTvEmoteId: '7tv-new-10', name: 'NewA' },
            { sevenTvEmoteId: '7tv-new-10', name: 'NewA' },
            { sevenTvEmoteId: '7tv-new-11', name: 'NewB' },
            { name: 'Broken' },
          ],
        }),
        'utf-8',
      ),
    });
    dialog = await waitForImportConfirmDialog(page);
    await expect(dialog.getByText('2 ungültige Zeilen in der Quelle verworfen.')).toBeVisible();
    await expect(dialog.getByText('1 doppelte Zeile in der Quelle zusammengefasst.')).toBeVisible();
    const dialogText = await dialog.innerText();
    expect(dialogText.indexOf('ungültige Zeilen in der Quelle verworfen')).toBeGreaterThan(-1);
    expect(dialogText.indexOf('doppelte Zeile in der Quelle zusammengefasst')).toBeGreaterThan(
      dialogText.indexOf('ungültige Zeilen in der Quelle verworfen'),
    );
    await expect(dialog.getByRole('button', { name: 'Kopieren' })).toBeEnabled();
  });
});

test.describe('file import dialog: shell contract', () => {
  test('opening the dialog focuses the file control, not the cancel button', async ({ page }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockMyChannels(page, [
      { channelName: SOURCE_CHANNEL, isBroadcaster: true, isTracked: true },
    ]);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);

    await gotoUsageStats(page, SOURCE_CHANNEL);

    const fileInput = await openFileImportDialog(page);

    // Plan §1.1 / design-language §7.3, open question 6: the file control is deliberately the
    // dialog's first focusable element, so the CDK's own `first-tabbable` default lands there with
    // no explicit `cdkFocusInitial`. A hidden `<input type="file">` cannot itself receive focus, so
    // the visible button in front of it is what the CDK actually focuses.
    await expect(
      page.getByRole('button', { name: 'Protokoll, Emote-Liste oder Nutzungs-Export auswählen' }),
    ).toBeFocused();
    // The input stays reachable through that button; asserted here so the two locators are not
    // silently talking about different elements.
    await expect(fileInput).toBeAttached();
  });

  test('lists the three acceptable file sorts before the file control', async ({ page }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockMyChannels(page, [
      { channelName: SOURCE_CHANNEL, isBroadcaster: true, isTracked: true },
    ]);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);

    await gotoUsageStats(page, SOURCE_CHANNEL);

    await openFileImportDialog(page);

    // §7.3's body-order contract (plan §1.1: heading, the three file-sort list items, THEN the file
    // control): the first list entry's own text must precede the file control's label in the
    // rendered DOM order, same pattern as the discardedRows/duplicatesCollapsed ordering check above
    // (`:492-496`).
    const dialogText = await page.getByRole('dialog').innerText();
    const sortsIndex = dialogText.indexOf('Purge-Protokoll (Wiederherstellen) als JSON');
    const controlIndex = dialogText.indexOf(
      'Protokoll, Emote-Liste oder Nutzungs-Export auswählen',
    );
    expect(sortsIndex).toBeGreaterThan(-1);
    expect(controlIndex).toBeGreaterThan(sortsIndex);
  });
});

test.describe('push flow: rejection', () => {
  test('a voting export and a foreign purge protocol are both refused with the existing errors', async ({
    page,
  }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockMyChannels(page, [
      { channelName: SOURCE_CHANNEL, isBroadcaster: true, isTracked: true },
    ]);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);

    await gotoUsageStats(page, SOURCE_CHANNEL);

    const fileInput = await openFileImportDialog(page);
    const dialog = page.getByRole('dialog');

    // A voting export: no import path exists for it at all — parseImportSource's votingExport
    // branch runs before the emote-list/usage dispatch even applies. Unlike the success path
    // (`push flow: the file path`), a rejection keeps the file-import dialog OPEN with the error as
    // a banner inside it — there is exactly one dialog throughout, never zero.
    await fileInput.setInputFiles({
      name: 'emotepurge_sensitron_voting_2026-08-01.json',
      mimeType: 'application/json',
      buffer: Buffer.from(
        JSON.stringify({
          source: 'emotepurge',
          kind: 'voting',
          formatVersion: 1,
          exportedAt: '2026-08-01T10:00:00Z',
          channelName: 'sensitron',
          withheld: [],
          meta: {},
          rows: [],
        }),
        'utf-8',
      ),
    });
    await expect(page.getByRole('dialog')).toHaveCount(1);
    await expect(dialog.getByRole('alert')).toContainText(
      'Das ist ein Export einer Abstimmung, kein Purge-Protokoll.',
    );

    // Regression guard for the restore branch (unchanged by #72): a purge protocol from THIS
    // channel but a DIFFERENT (now inactive) emote set is rejected as wrongSet, not silently routed
    // through the new import path. Still the SAME dialog — a second failure does not need (and does
    // not get) a fresh open.
    await fileInput.setInputFiles({
      name: 'emotepurge_sensitron_purge_202608011200.json',
      mimeType: 'application/json',
      buffer: Buffer.from(
        JSON.stringify({
          source: 'emotepurge',
          kind: 'purge-run',
          formatVersion: 1,
          exportedAt: '2026-08-01T12:00:00Z',
          channelName: 'sensitron',
          withheld: [],
          meta: {
            emoteSetId: 'a-long-gone-set',
            startedAt: '2026-08-01T12:00:00Z',
            finishedAt: '2026-08-01T12:05:00Z',
            counts: { requested: 1, succeeded: 1, failed: 0, cancelled: 0 },
          },
          rows: [
            {
              emoteId: 'i1',
              sevenTvEmoteId: '7tv-1',
              name: 'PogU',
              status: 'done',
              errorMessage: null,
            },
          ],
        }),
        'utf-8',
      ),
    });
    await expect(page.getByRole('dialog')).toHaveCount(1);
    await expect(dialog.getByRole('alert')).toContainText(
      'Das Protokoll gehört zu einem anderen Emote-Set — der Channel hat das aktive Set gewechselt.',
    );
  });
});

test.describe('running import: channel switch', () => {
  // R11/R9: the dock is a root-provided-service view, not page state, so it must survive a
  // navigation to a different channel's usage-stats page, and it must keep naming the run's target
  // channel there rather than reading as "a run of THIS page". Also exercises the "open target
  // channel" link (the only in-app, same-route channel-to-channel navigation anywhere in the app)
  // end to end: a full page reload would lose the root-provided SevenTvImportService's state
  // entirely, so the dock surviving on the new URL is itself proof this was an Angular soft
  // navigation, not a reload.
  //
  // What this does NOT establish (see the final report): whether the leave guard's `leadsToSameRoute`
  // exemption (usage-stats-leave.guard.ts) is reachable while `isRunning()` is still true. The one
  // link that goes from one channel's usage-stats page straight to another's
  // (`app-import-progress-section`'s "Zielkanal öffnen") only renders once the run has settled
  // (`run-progress-panel.ts`: the run-actions slot is gated on `!isRunning()`), so by the time it is
  // clickable the guard's own first check (`if (!importService.isRunning()) return of(true)`)
  // already lets the navigation through unconditionally — the same-route comparison never runs for
  // this particular click. No other UI affordance in the app links two channels' usage-stats pages
  // directly (the overview page does, but only via a different route in between), so that ONE
  // exemption has no reachable trigger through the browser and stays unit-test-only. The branch
  // that asks does have one and is covered below ('leaving the page').
  test('a settled run keeps its target line and its progress on the target channel page', async ({
    page,
  }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockMyChannels(page, [
      { channelName: SOURCE_CHANNEL, isBroadcaster: true, isTracked: true },
      { channelName: TARGET_CHANNEL, isSevenTvEditor: true, isTracked: true },
    ]);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);
    await mockWorkspace(page, TARGET_CHANNEL, [], 'target-set');
    await mockSetWarning(page, TARGET_CHANNEL);
    await mockEmoteList(page, TARGET_CHANNEL, []);
    await mockSyncImported(page, TARGET_CHANNEL);
    await mockChannelScopedResync(page, TARGET_CHANNEL);

    // R14: seeds the write token via addInitScript and routes 7tv.io's GQL endpoint — must be
    // registered, like installLiveStub, before the first goto.
    await mockSevenTvGql(page, () => ({ data: { emoteSet: { emotes: [{ id: '7tv-1' }] } } }));
    // Frozen from the start: the run engine paces every row with a trailing RUN_DELAY_MS timer
    // (seven-tv-run-engine.ts), and a real wait would race it. runFor() below drives it explicitly.
    await page.clock.install();

    await gotoUsageStats(page, SOURCE_CHANNEL);

    await cell(page, 'CatJAM').click();
    await copyButton(page).click();

    let dialog = page.getByRole('dialog');
    await dialog.getByRole('radio', { name: '#aatrociity' }).check();
    await dialog.getByRole('button', { name: 'Weiter' }).click();

    dialog = page.getByRole('dialog');
    await expect(dialog.locator('#app-dialog-title')).toHaveText(
      '1 Emote nach aatrociity kopieren?',
    );
    await dialog.getByRole('button', { name: 'Kopieren' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    // Lets the one queued row's HTTP call resolve and its trailing pacing delay elapse, which is
    // what the engine's `finish()` waits on even for a single-row run.
    await page.clock.runFor(1000);

    await expect(page.getByText('Ziel: aatrociity')).toBeVisible();
    await expect(page.getByText('1 kopiert · 0 fehlgeschlagen · 0 abgebrochen')).toBeVisible();

    const openTargetLink = page.getByRole('link', { name: 'Zielkanal öffnen' });
    await expect(openTargetLink).toBeVisible();
    await openTargetLink.click();

    await page.waitForURL(`**/channels/${TARGET_CHANNEL}/usage-stats`);
    // No confirm dialog interrupted the navigation — the guard's own isRunning() check already
    // lets a settled run's navigation straight through (see the test-level comment above).
    await expect(page.getByRole('dialog')).toHaveCount(0);
    await expect(page.getByRole('heading', { name: 'Emote-Nutzung' })).toBeVisible();

    // Still there on the new page, with the same target line — proof the dock reads off the
    // root-provided service rather than off this page's own channel (R9), and proof this really was
    // an in-app navigation rather than a reload (a reload would have reset the service to no run at
    // all, and none of the following would render).
    await expect(page.getByText('Ziel: aatrociity')).toBeVisible();
    await expect(page.getByText('1 kopiert · 0 fehlgeschlagen · 0 abgebrochen')).toBeVisible();
  });

  /**
   * The same link, used as the only reachable trigger for a channel switch *inside* the usage-stats
   * route — and therefore the only way to reach the window this test is about.
   *
   * `channelName()` follows the URL at once while the set status and the totals keep describing the
   * previous channel until their own responses land. The copy button used to stay live throughout:
   * it hangs on `atlasOrder().length` and the run arbiter, neither of which notices a channel
   * switch. A click in that window captured channel B's name together with channel A's rows and A's
   * `sourceEmoteSetId` — the confirm dialog said "origin: b", the saved file was named after b, and
   * a run copied A's emotes into a third set under B's name. That is a wrong 7TV write, not a
   * display glitch.
   *
   * Both halves are asserted separately, because they resolve independently and a fix that only
   * waited for the set status would still pass the first: after the status lands the button must
   * STILL be locked, since the grid underneath is the previous channel's until the totals answer.
   * (Which is also why `isLoading()` alone is not the condition — in the other response order it is
   * already false while the set id is still the old one.)
   */
  test('the copy button stays locked until BOTH the set status and the rows are the new channel’s', async ({
    page,
  }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockMyChannels(page, [
      { channelName: SOURCE_CHANNEL, isBroadcaster: true, isTracked: true },
      { channelName: TARGET_CHANNEL, isSevenTvEditor: true, isTracked: true },
    ]);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);
    // The target needs rows of its own: without them the button would end up disabled on
    // `atlasOrder().length === 0` and the final assertion could not tell the fix from an empty grid.
    await mockWorkspace(page, TARGET_CHANNEL, TARGET_EMOTES, 'target-set');
    await mockSetWarning(page, TARGET_CHANNEL);
    await mockEmoteList(page, TARGET_CHANNEL, []);
    await mockSyncImported(page, TARGET_CHANNEL);
    await mockChannelScopedResync(page, TARGET_CHANNEL);

    await mockSevenTvGql(page, () => ({ data: { emoteSet: { emotes: [{ id: '7tv-1' }] } } }));
    await page.clock.install();

    await gotoUsageStats(page, SOURCE_CHANNEL);

    // A minimal run, only to reach the settled state that renders the "open target channel" link.
    // Runs BEFORE the two routes below are deferred: the confirm dialog's own `loadImportTarget`
    // resolves the very same target-channel endpoints (`getSetStatus` → `emotes/active-set`) to
    // decide when "Kopieren" may be clicked, and deferring them any earlier would starve the
    // dialog itself, not just the post-switch page load this test is actually about.
    await cell(page, 'CatJAM').click();
    await copyButton(page).click();
    let dialog = page.getByRole('dialog');
    await dialog.getByRole('radio', { name: '#aatrociity' }).check();
    await dialog.getByRole('button', { name: 'Weiter' }).click();
    dialog = page.getByRole('dialog');
    await dialog.getByRole('button', { name: 'Kopieren' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);
    await page.clock.runFor(1000);

    // On the source page, with everything current, the button is live — the baseline the two
    // assertions below are a change from. The CatJAM row is still selected too (nothing about
    // finishing an import run clears the selection, unlike a delete — see onDeleted vs.
    // startImportFromChoice), so the dock shortcut is live as well: importShortcutLocked shares
    // importScopeCurrent with the header button, and this is the one other trigger built on it.
    await expect(copyButton(page)).toBeEnabled();
    await expect(dockCopyButton(page, 1)).toBeEnabled();

    // Registered only now, so the two routes above answer normally for the confirm dialog's own
    // load and are held only for the page navigation triggered below (see deferRoute).
    const releaseTargetStatus = await deferRoute(
      page,
      `**/api/channels/${TARGET_CHANNEL}/emotes/active-set`,
    );
    const releaseTargetTotals = await deferRoute(
      page,
      `**/api/channels/${TARGET_CHANNEL}/usage-stats/totals**`,
    );

    await page.getByRole('link', { name: 'Zielkanal öffnen' }).click();
    await page.waitForURL(`**/channels/${TARGET_CHANNEL}/usage-stats`);

    // Still mounted, because activeEmoteSetId() is the SOURCE channel's set — which is precisely
    // the state that must not be copyable. The dock shortcut shares the same lock (importScopeCurrent)
    // and must therefore be just as disabled, not only the header's own trigger.
    await expect(copyButton(page)).toBeVisible();
    await expect(copyButton(page)).toBeDisabled();
    await expect(dockCopyButton(page, 1)).toBeDisabled();

    // The totals request is only issued once the set status has resolved the "all time" range, so
    // waiting for it is exact proof that the set status half has landed and the rows half has not.
    const totalsRequested = page.waitForRequest(
      `**/api/channels/${TARGET_CHANNEL}/usage-stats/totals**`,
    );
    releaseTargetStatus();
    await totalsRequested;
    await expect(copyButton(page)).toBeDisabled();
    await expect(dockCopyButton(page, 1)).toBeDisabled();

    // Only the header button is checked for "live again" below: the totals load that follows
    // clears the selection (loadTotals without preserveSelection — see the effect above), so the
    // dock shortcut goes on to lock for its OTHER reason, an empty selection, which is a separate,
    // already-covered concern (importShortcutDisabled) rather than a second data point on the
    // channel-switch lock this test is about.
    releaseTargetTotals();
    await expect(cell(page, 'Sadge')).toBeVisible();
    await expect(copyButton(page)).toBeEnabled();
  });
});

test.describe('running import: a token without write rights', () => {
  /**
   * The `abortOn` detour (`abortsForMissingPrivileges`) end to end. Two things are asserted that no
   * unit test can see together: that the run really stops after the FIRST row rather than burning
   * the whole selection against the same refusal, and — the part that slips through most easily —
   * that no follow-up call goes out afterwards. `onRunComplete` returns early on an empty
   * `doneKeys`, so neither the audit report (`sync-imported`) nor the target resync may fire; both
   * are routed here rather than left unmocked, so a stray call is counted instead of dying in the
   * dev proxy and being mistaken for "nothing happened".
   */
  test('stops after the first row, cancels the rest and reports nothing back', async ({ page }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockMyChannels(page, [
      { channelName: SOURCE_CHANNEL, isBroadcaster: true, isTracked: true },
      { channelName: TARGET_CHANNEL, isSevenTvEditor: true, isTracked: true },
    ]);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);
    await mockActiveEmoteSet(page, TARGET_CHANNEL, 'target-set', {
      capacity: 1000,
      occupiedSlots: 3,
    });
    await mockSetWarning(page, TARGET_CHANNEL);
    // Empty target set: both selected rows survive the already-present filter, so the run has a
    // second row that the abort has to cancel.
    await mockEmoteList(page, TARGET_CHANNEL, []);

    const followUps: string[] = [];
    await page.route(`**/api/channels/${TARGET_CHANNEL}/emotes/sync-imported`, (route) => {
      followUps.push('sync-imported');
      return route.fulfill({ status: 204 });
    });
    await page.route(`**/api/channels/${TARGET_CHANNEL}/resync`, (route) => {
      followUps.push('resync');
      return route.fulfill({ status: 202 });
    });

    // 7TV's own wording for a token that may not write the set (PRIVILEGE_ERROR_FRAGMENTS), sent
    // as a GQL error inside a 200 — which is how 7TV actually reports it.
    let mutationCount = 0;
    await mockSevenTvGql(page, () => {
      mutationCount += 1;
      return { errors: [{ message: 'insufficient privileges for this emote set' }] };
    });
    await page.clock.install();

    await gotoUsageStats(page, SOURCE_CHANNEL);

    await cell(page, 'CatJAM').click();
    await cell(page, 'KEKW').click({ modifiers: ['Shift'] });
    await copyButton(page).click();

    let dialog = page.getByRole('dialog');
    await dialog.getByRole('radio', { name: '#aatrociity' }).check();
    await dialog.getByRole('button', { name: 'Weiter' }).click();

    dialog = page.getByRole('dialog');
    await expect(dialog.locator('#app-dialog-title')).toHaveText(
      '2 Emotes nach aatrociity kopieren?',
    );
    await dialog.getByRole('button', { name: 'Kopieren' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    // Generous: the first row's failure, the abort detour and the engine's own settling all sit
    // behind the frozen pacing timers, and a rate-limit retry would too (it is not one here).
    await page.clock.runFor(5000);

    const section = page.locator('app-import-progress-section');
    await expect(section.getByText('0 kopiert · 1 fehlgeschlagen · 1 abgebrochen')).toBeVisible();
    await expect(
      section.getByText(
        'Das 7TV-Token hat im Zielset kein Schreibrecht — der Lauf wurde nach der ersten Zeile abgebrochen.',
      ),
    ).toBeVisible();

    // One attempt, not two: the second row never reached 7TV's rate-limit bucket.
    expect(mutationCount).toBe(1);
    expect(followUps).toEqual([]);
  });
});

test.describe('running import: leaving the page', () => {
  /**
   * The half of R11 that asks (`usageStatsLeaveGuard`). Its exemption for a pure channel switch has
   * no reachable trigger in the browser (see the channel-switch test above), but the branch that
   * *asks* has one: any navigation to a different KIND of page while the run is still going — here
   * the workspace's own "Votings" tab, which is a different route definition and therefore not the
   * same-route reuse the guard exempts. Cancelling stays put, confirming leaves; the run is
   * untouched either way, the guard only asks.
   */
  test('a navigation to another page asks first, and only a confirmation leaves', async ({
    page,
  }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockMyChannels(page, [
      { channelName: SOURCE_CHANNEL, isBroadcaster: true, isTracked: true },
      { channelName: TARGET_CHANNEL, isSevenTvEditor: true, isTracked: true },
    ]);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);
    await mockActiveEmoteSet(page, TARGET_CHANNEL, 'target-set', {
      capacity: 1000,
      occupiedSlots: 3,
    });
    await mockSetWarning(page, TARGET_CHANNEL);
    await mockEmoteList(page, TARGET_CHANNEL, []);
    await mockSyncImported(page, TARGET_CHANNEL);
    await mockChannelScopedResync(page, TARGET_CHANNEL);
    await mockVoteSessionList(page, SOURCE_CHANNEL, []);

    await mockSevenTvGql(page, () => ({ data: { emoteSet: { emotes: [{ id: '7tv-1' }] } } }));
    // Frozen and never advanced in this test: the engine's trailing pacing delay is what keeps the
    // run in flight, so `isRunning()` stays true for as long as the clock does not move — which is
    // exactly the state the guard is written for.
    await page.clock.install();

    await gotoUsageStats(page, SOURCE_CHANNEL);

    await cell(page, 'CatJAM').click();
    await cell(page, 'KEKW').click({ modifiers: ['Shift'] });
    await copyButton(page).click();

    let dialog = page.getByRole('dialog');
    await dialog.getByRole('radio', { name: '#aatrociity' }).check();
    await dialog.getByRole('button', { name: 'Weiter' }).click();

    dialog = page.getByRole('dialog');
    await dialog.getByRole('button', { name: 'Kopieren' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    // The run is live: the panel offers to cancel it, which it only does while running.
    const section = page.locator('app-import-progress-section');
    await expect(section.getByRole('button', { name: 'Abbrechen' })).toBeVisible();

    await page.getByRole('link', { name: 'Votings' }).click();

    const leavePrompt = page.getByRole('dialog');
    await expect(leavePrompt).toContainText('Der Kopierlauf läuft noch.');
    await leavePrompt.getByRole('button', { name: 'Abbrechen' }).click();

    // Declining keeps the page AND the run — the guard never touches the service.
    await expect(page.getByRole('dialog')).toHaveCount(0);
    expect(new URL(page.url()).pathname).toBe(`/channels/${SOURCE_CHANNEL}/usage-stats`);
    await expect(section.getByRole('button', { name: 'Abbrechen' })).toBeVisible();

    await page.getByRole('link', { name: 'Votings' }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Verlassen' }).click();

    await page.waitForURL(`**/channels/${SOURCE_CHANNEL}/vote-sessions`);
    await expect(page.getByRole('dialog')).toHaveCount(0);
  });
});
