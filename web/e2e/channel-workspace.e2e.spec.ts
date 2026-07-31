import { expect, test } from '@playwright/test';

import {
  AUTH_USER,
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
});
