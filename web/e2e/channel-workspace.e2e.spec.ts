import { expect, test } from '@playwright/test';

import {
  AUTH_USER,
  emitLive,
  installLiveStub,
  mockActiveEmoteSet,
  mockAuthMe,
  mockChannelPermissions,
  mockChannelScopedResync,
  mockChannelStatus,
  mockDuplicateEmoteNames,
  mockMyChannels,
  mockUsageTotals,
  mockWorkerHealth,
} from './support/mocks';

test.describe('authenticated broadcaster', () => {
  test.beforeEach(async ({ page }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page, 'connected');
    // The usage-stats page opens /api/channels/{name}/live on mount; without the stub it would
    // reconnect-loop against a route no mock serves.
    await installLiveStub(page);
  });

  test('sees their display name and logout button in the header on the overview', async ({
    page,
  }) => {
    await mockMyChannels(page, [
      { channelName: 'sensitron', isBroadcaster: true, isTracked: true, isBotActive: true },
    ]);

    await page.goto('/');

    await expect(page).toHaveURL('/');
    await expect(page.getByText('Sensitron', { exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Logout' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Login' })).toHaveCount(0);
  });

  test('opens a tracked channel from the overview and sees its usage stats', async ({ page }) => {
    await mockMyChannels(page, [
      { channelName: 'sensitron', isBroadcaster: true, isTracked: true, isBotActive: true },
    ]);
    await mockChannelPermissions(page, 'sensitron');
    await mockChannelStatus(page, 'sensitron');
    await mockActiveEmoteSet(page, 'sensitron');
    await mockUsageTotals(page, 'sensitron', [
      {
        emoteId: 'e1',
        emoteName: 'PogU',
        sevenTvEmoteId: '7tv-1',
        imageUrl: 'https://cdn.7tv.app/emote/1/1x.webp',
        totalUseCount: 42,
      },
    ]);

    await page.goto('/');
    // The channel name is the card's stretched-link anchor; the extra "Öffnen" button is gone.
    await page.getByRole('link', { name: '#sensitron' }).click();

    await expect(page).toHaveURL(/\/channels\/sensitron\/usage-stats$/);
    await expect(page.getByRole('heading', { name: 'Emote-Nutzung' })).toBeVisible();
    await expect(page.getByText('PogU')).toBeVisible();
    await expect(page.getByText('42x')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Channel verlassen' })).toBeVisible();
  });

  test('warns about duplicate emote names and reveals the colliding emotes on demand', async ({
    page,
  }) => {
    await mockChannelPermissions(page, 'sensitron');
    await mockChannelStatus(page, 'sensitron');
    await mockActiveEmoteSet(page, 'sensitron');
    await mockUsageTotals(page, 'sensitron', []);
    await mockDuplicateEmoteNames(page, 'sensitron', [
      {
        name: 'ApuDrums',
        emotes: [
          {
            emoteId: 'e-dup-1',
            sevenTvEmoteId: '7tv-dup-1',
            imageUrl: 'https://cdn.7tv.app/emote/1/1x.webp',
          },
          {
            emoteId: 'e-dup-2',
            sevenTvEmoteId: '7tv-dup-2',
            imageUrl: 'https://cdn.7tv.app/emote/2/1x.webp',
          },
        ],
      },
    ]);

    await page.goto('/channels/sensitron/usage-stats');

    await expect(
      page.getByText('Ein Emote-Name ist im aktiven 7TV-Set mehrfach vergeben.'),
    ).toBeVisible();
    // Collapsed by default: the colliding name only appears after opening the details.
    await expect(page.getByText('ApuDrums')).toHaveCount(0);

    await page.getByRole('button', { name: 'Details anzeigen' }).click();

    await expect(page.getByText('ApuDrums')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Details ausblenden' })).toBeVisible();
  });

  test('shows no duplicate-name banner when every active emote name is unique', async ({
    page,
  }) => {
    await mockChannelPermissions(page, 'sensitron');
    await mockChannelStatus(page, 'sensitron');
    await mockActiveEmoteSet(page, 'sensitron');
    await mockUsageTotals(page, 'sensitron', []);
    await mockDuplicateEmoteNames(page, 'sensitron', []);

    await page.goto('/channels/sensitron/usage-stats');

    await expect(page.getByRole('heading', { name: 'Emote-Nutzung' })).toBeVisible();
    await expect(page.getByText('mehrfach vergeben')).toHaveCount(0);
  });

  test('resync reports queued and upgrades to finished when the sync event arrives', async ({
    page,
  }) => {
    await mockChannelPermissions(page, 'sensitron');
    await mockActiveEmoteSet(page, 'sensitron');
    await mockUsageTotals(page, 'sensitron', []);
    await mockChannelScopedResync(page, 'sensitron');

    await page.goto('/channels/sensitron/usage-stats');
    await page.getByRole('button', { name: 'Neu synchronisieren' }).click();

    // The 202 only means the worker was told; the confirmation is a separate live event.
    // Located by text, not by role: the usage grid below carries its own role="status" counter.
    await expect(page.getByText('Resync angestoßen …')).toBeVisible();

    await emitLive(page, { type: 'channel.synced', channel: 'sensitron' });

    await expect(page.getByText('Resync abgeschlossen.')).toBeVisible();
  });

  // The endpoint sits behind the wider permission check on purpose: the person who just added an
  // emote and is wondering why it is missing is usually the channel's 7TV editor.
  test('a 7TV editor sees the resync button but not the leave button', async ({ page }) => {
    await mockChannelPermissions(page, 'sensitron', {
      canManage: false,
      canViewUsageStats: true,
    });
    await mockActiveEmoteSet(page, 'sensitron');
    await mockUsageTotals(page, 'sensitron', []);

    await page.goto('/channels/sensitron/usage-stats');

    await expect(page.getByRole('button', { name: 'Neu synchronisieren' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Channel verlassen' })).toHaveCount(0);
  });

  test('a second resync inside the cooldown says so instead of failing silently', async ({
    page,
  }) => {
    await mockChannelPermissions(page, 'sensitron');
    await mockActiveEmoteSet(page, 'sensitron');
    await mockUsageTotals(page, 'sensitron', []);
    await mockChannelScopedResync(page, 'sensitron', 429);

    await page.goto('/channels/sensitron/usage-stats');
    await page.getByRole('button', { name: 'Neu synchronisieren' }).click();

    // The dedicated error code, not the rate limiter's generic 429 message — that difference is
    // the whole reason the cooldown answers with a body.
    await expect(
      page.getByText('Für diesen Channel läuft bereits ein Resync. Bitte kurz warten.'),
    ).toBeVisible();
  });

  // Would have caught a regression back to an inner scroll container: with one the window never
  // scrolls, with window scrolling the sticky layers must keep pinning (design doc §8.5).
  test('header, tabs and filter toolbar stay pinned while the emote grid scrolls with the page', async ({
    page,
  }) => {
    await mockChannelPermissions(page, 'sensitron');
    await mockChannelStatus(page, 'sensitron');
    await mockActiveEmoteSet(page, 'sensitron');
    await mockUsageTotals(
      page,
      'sensitron',
      Array.from({ length: 60 }, (_, i) => ({
        emoteId: `e${i}`,
        emoteName: `Emote${i}`,
        sevenTvEmoteId: `7tv-${i}`,
        imageUrl: 'https://cdn.7tv.app/emote/1/1x.webp',
        totalUseCount: 60 - i,
      })),
    );

    await page.goto('/channels/sensitron/usage-stats');
    await expect(page.getByText('Emote0', { exact: true })).toBeVisible();

    await page.evaluate(() => window.scrollTo(0, document.body.scrollHeight));
    expect(await page.evaluate(() => window.scrollY)).toBeGreaterThan(0);

    // The three sticky layers: shell header, workspace tab bar, filter toolbar...
    await expect(page.getByRole('link', { name: 'Emote Purge' })).toBeInViewport();
    await expect(page.getByRole('link', { name: 'Nutzung' })).toBeInViewport();
    await expect(page.getByRole('textbox', { name: 'Name suchen…' })).toBeInViewport();
    // ...while the channel title above them scrolls away like normal content.
    await expect(page.getByRole('heading', { level: 1 })).not.toBeInViewport();
  });
});
