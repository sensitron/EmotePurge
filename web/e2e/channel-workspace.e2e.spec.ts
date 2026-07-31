import { expect, test } from '@playwright/test';

import {
  AUTH_USER,
  installLiveStub,
  mockActiveEmoteSet,
  mockAuthMe,
  mockChannelPermissions,
  mockChannelStatus,
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
    // "Öffnen" is a stretched-link anchor since the clickable-cards rework, not a button.
    await page.getByRole('link', { name: 'Öffnen' }).click();

    await expect(page).toHaveURL(/\/channels\/sensitron\/usage-stats$/);
    await expect(page.getByRole('heading', { name: 'Emote-Nutzung' })).toBeVisible();
    await expect(page.getByText('PogU')).toBeVisible();
    await expect(page.getByText('42x')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Channel verlassen' })).toBeVisible();
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
