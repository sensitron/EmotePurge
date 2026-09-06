import { Page, expect, test } from '@playwright/test';

import {
  AUTH_USER,
  MockEmoteUsage,
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

const copyButton = (page: Page) => page.getByRole('button', { name: 'In Kanal kopieren…' });

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
    await expect(picker.locator('#app-dialog-title')).toHaveText('Emotes in einen Kanal kopieren');

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
});

test.describe('push flow: the file path', () => {
  // Both an emote-list and a usage export lead into the same confirmation dialog, uploaded through
  // the restore panel's file input (not the picker's "save as file" option) — the panel dispatches
  // on the envelope's `kind` (RestorePanel.onFileSelected). The target here is always the CURRENT
  // channel: the restore panel always imports into `channelName()`.
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

    const fileInput = page.locator('input[type="file"]');

    // File 1: an emote-list export from THIS channel, both rows already in the target — both
    // sameChannelFile and nothingToAdd apply, and the execute button is locked.
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
    let dialog = page.getByRole('dialog');
    await dialog.locator('#app-dialog-title').waitFor();
    await expect(dialog.getByText('Diese Liste stammt aus diesem Kanal.')).toBeVisible();
    await expect(dialog.getByText('Alle 2 Emotes sind bereits im Zielset.')).toBeVisible();
    await expect(dialog.getByRole('button', { name: 'Kopieren' })).toBeDisabled();
    await dialog.getByRole('button', { name: 'Abbrechen' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    // File 2: a usage export with no `exportedAt` at all — the date reads as unknown rather than
    // crashing or silently defaulting to "now".
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
    dialog = page.getByRole('dialog');
    await dialog.locator('#app-dialog-title').waitFor();
    await expect(dialog.getByText('Export aus sensitron, Datum unbekannt')).toBeVisible();
    await dialog.getByRole('button', { name: 'Abbrechen' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    // File 3: meta.rowCount claims 5 rows, but the array itself carries 4 — one structurally
    // invalid (no sevenTvEmoteId) and one valid duplicate of another valid row. discardedRows is
    // measured against the claimed count (5 - 3 structurally valid = 2, R6/2.7), independent of
    // duplicatesCollapsed (1, from the dedup that runs after validity filtering) — and the
    // contract (T5) puts the discarded-rows line before the duplicates-collapsed line.
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
    dialog = page.getByRole('dialog');
    await dialog.locator('#app-dialog-title').waitFor();
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

    const fileInput = page.locator('input[type="file"]');

    // A voting export: no import path exists for it at all — parseImportSource's votingExport
    // branch runs before the emote-list/usage dispatch even applies.
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
    await expect(page.getByRole('alert')).toContainText(
      'Das ist ein Export einer Abstimmung, kein Purge-Protokoll.',
    );
    await expect(page.getByRole('dialog')).toHaveCount(0);

    // Regression guard for the restore branch (unchanged by #72): a purge protocol from THIS
    // channel but a DIFFERENT (now inactive) emote set is rejected as wrongSet, not silently routed
    // through the new import path.
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
    await expect(page.getByRole('alert')).toContainText(
      'Das Protokoll gehört zu einem anderen Emote-Set — der Channel hat das aktive Set gewechselt.',
    );
    await expect(page.getByRole('dialog')).toHaveCount(0);
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
