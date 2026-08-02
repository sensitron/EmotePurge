import { expect, test } from '@playwright/test';

import {
  AUTH_USER,
  installLiveStub,
  mockAuthMe,
  mockChannelAuditLog,
  mockChannelPermissions,
  mockWorkerHealth,
} from './support/mocks';

test.describe('channel activity tab', () => {
  test.beforeEach(async ({ page }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page, 'connected');
    await installLiveStub(page);
  });

  test('a manager sees the tab and its rows with actor, action and detail', async ({ page }) => {
    await mockChannelPermissions(page, 'sensitron');
    await mockChannelAuditLog(page, 'sensitron', {
      1: [
        { id: 3, action: 'channel.join', actorLogin: 'sensitron' },
        {
          id: 2,
          action: 'voteSession.delete',
          actorLogin: 'somemod',
          detail: { kind: 'title', count: null, text: 'Sommer-Purge' },
        },
        {
          id: 1,
          action: 'emotes.syncDeleted',
          detail: { kind: 'emoteCount', count: 12, text: null },
        },
      ],
    });

    await page.goto('/channels/sensitron/activity');

    await expect(page.getByRole('heading', { name: 'Aktivitätsverlauf' })).toBeVisible();
    // The question the feature exists to answer: which moderator deleted the voting.
    await expect(page.getByRole('listitem').getByText('Abstimmung gelöscht')).toBeVisible();
    await expect(page.getByText('von somemod')).toBeVisible();
    await expect(page.getByText('„Sommer-Purge“')).toBeVisible();
    await expect(page.getByText('12 Emotes')).toBeVisible();
  });

  test('rows carry no channel link, unlike the admin log', async ({ page }) => {
    await mockChannelPermissions(page, 'sensitron');
    await mockChannelAuditLog(page, 'sensitron', {
      1: [{ id: 1, action: 'channel.join' }],
    });

    await page.goto('/channels/sensitron/activity');
    await expect(page.getByRole('listitem').first()).toBeVisible();

    // Every row is this channel; a link would point at the page the reader is already on.
    await expect(page.getByRole('link', { name: '#sensitron' })).toHaveCount(0);
  });

  test('the action filter offers no user-scoped actions', async ({ page }) => {
    await mockChannelPermissions(page, 'sensitron');
    await mockChannelAuditLog(page, 'sensitron', { 1: [{ id: 1, action: 'channel.join' }] });

    await page.goto('/channels/sensitron/activity');

    await expect(page.getByRole('radio', { name: 'Channel beigetreten' })).toBeVisible();
    // These two can never appear in a single channel's log — offering them would be a segment that
    // only ever returns nothing.
    await expect(page.getByRole('radio', { name: 'User-Sessions widerrufen' })).toHaveCount(0);
    await expect(page.getByRole('radio', { name: 'Rollen-Cache geleert' })).toHaveCount(0);
  });

  test('filtering by action narrows the list', async ({ page }) => {
    await mockChannelPermissions(page, 'sensitron');
    await mockChannelAuditLog(page, 'sensitron', {
      1: [
        { id: 2, action: 'channel.join' },
        { id: 1, action: 'voteSession.delete' },
      ],
    });

    await page.goto('/channels/sensitron/activity');
    await expect(page.getByRole('listitem')).toHaveCount(2);

    await page.getByRole('radio', { name: 'Abstimmung gelöscht' }).click();

    await expect(page.getByRole('listitem')).toHaveCount(1);
    await expect(page.getByRole('listitem').getByText('Abstimmung gelöscht')).toBeVisible();
  });

  // The permission boundary that separates this tab from every other channel page: a 7TV editor may
  // read the usage stats and must not read who on the mod team did what.
  test('a 7TV editor sees neither the tab nor the page', async ({ page }) => {
    await mockChannelPermissions(page, 'sensitron', {
      canManage: false,
      canViewUsageStats: true,
    });

    await page.goto('/channels/sensitron/vote-sessions');
    await expect(page.getByRole('link', { name: 'Votings' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Aktivität' })).toHaveCount(0);

    await page.goto('/channels/sensitron/activity');
    await expect(page).toHaveURL(/\/channels\/sensitron\/vote-sessions$/);
  });
});
