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

  // The one thing jsdom cannot check: there is no layout there, so the unit spec can only assert that
  // the pager calls scrollIntoView. Whether the reader actually ends up at the top of the new page —
  // and in front of it rather than behind the sticky stack — needs a real browser (design doc §8.4).
  test('a page change puts the reader back at the top of the results, clear of the sticky bars', async ({
    page,
  }) => {
    await mockChannelPermissions(page, 'sensitron');
    await mockChannelAuditLog(page, 'sensitron', {
      1: Array.from({ length: 25 }, (_, i) => ({ id: 100 - i, action: 'channel.join' })),
      2: Array.from({ length: 5 }, (_, i) => ({ id: 20 - i, action: 'voteSession.delete' })),
    });

    await page.goto('/channels/sensitron/activity');
    await expect(page.getByRole('listitem')).toHaveCount(25);

    // Where the click always happens: the pager sits under the list, so reaching it means being at
    // the bottom of the document.
    await page.evaluate(() => window.scrollTo(0, document.body.scrollHeight));
    const scrolledDown = await page.evaluate(() => window.scrollY);
    expect(scrolledDown).toBeGreaterThan(0);

    await page.getByRole('navigation', { name: 'Seitennummerierung' }).getByText('Weiter').click();
    await expect(page.getByRole('listitem')).toHaveCount(5);

    expect(await page.evaluate(() => window.scrollY)).toBeLessThan(scrolledDown);

    // Not just "scrolled up" but "not behind the header": scroll-mt-24 keeps the heading clear of the
    // shell header (h-14) plus the workspace tabs (h-10), which is what a bare scrollIntoView misses.
    const heading = page.getByRole('heading', { name: 'Aktivitätsverlauf' });
    await expect(heading).toBeInViewport();
    const box = await heading.boundingBox();
    expect(box!.y).toBeGreaterThanOrEqual(96);

    // And the keyboard user came along — without this the focus would have fallen to <body> when the
    // clicked button left the DOM with the old page.
    await expect(heading).toBeFocused();
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
