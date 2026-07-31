import { expect, test } from '@playwright/test';

import {
  AUTH_USER,
  mockAdminChannelList,
  mockAuthMe,
  mockChannelResync,
  mockMyChannels,
  mockPurge,
  mockWorkerHealth,
} from './support/mocks';

const ADMIN_USER = { ...AUTH_USER, isGlobalAdmin: true };

test.describe('global admin on /admin/channels', () => {
  test.beforeEach(async ({ page }) => {
    await mockAuthMe(page, ADMIN_USER);
    await mockWorkerHealth(page, 'connected');
  });

  test('sees every tracked channel with its aggregates', async ({ page }) => {
    await mockAdminChannelList(page, [
      {
        channelName: 'handofblood',
        emoteCount: 903,
        archivedEmoteCount: 17,
        voteSessionCount: 4,
        activeVoteSessionCount: 1,
        lastSyncedAtUtc: '2026-07-31T11:00:00Z',
      },
      { channelName: 'sensitron', isBotActive: false },
    ]);

    await page.goto('/admin/channels');

    await expect(page.getByRole('link', { name: '#handofblood' })).toBeVisible();
    await expect(page.getByRole('link', { name: '#sensitron' })).toBeVisible();
    // Total/subset pairing, straight from the server — no client-side arithmetic.
    await expect(page.getByText('903 Emotes (17 archiviert)')).toBeVisible();
    await expect(page.getByText('4 Votings (1 aktiv)')).toBeVisible();
    // The inactive channel offers "Beitreten", the active one "Verlassen".
    await expect(page.getByRole('button', { name: 'Beitreten' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Verlassen' })).toBeVisible();
  });

  test('resync is offered on the active channel only, and POSTs with a transient confirmation', async ({
    page,
  }) => {
    await mockAdminChannelList(page, [
      { channelName: 'handofblood', isBotActive: true },
      { channelName: 'sensitron', isBotActive: false },
    ]);
    await mockChannelResync(page, 'handofblood');

    await page.goto('/admin/channels');

    const activeRow = page.getByRole('listitem').filter({ hasText: '#handofblood' });
    const inactiveRow = page.getByRole('listitem').filter({ hasText: '#sensitron' });

    // The worker resolves the command against its joined channels, so an inactive row must not even
    // offer the button — a 409 is not a useful thing to hand an admin.
    await expect(activeRow.getByRole('button', { name: 'Resync' })).toBeVisible();
    await expect(inactiveRow.getByRole('button', { name: 'Resync' })).toHaveCount(0);

    const resyncRequest = page.waitForRequest(
      (request) =>
        request.method() === 'POST' &&
        request.url().includes('/api/admin/channels/handofblood/resync'),
    );
    await activeRow.getByRole('button', { name: 'Resync' }).click();
    await resyncRequest;

    // 202 means "queued": nothing on the row changes, so the inline hint is the only feedback.
    await expect(activeRow.getByText('Resync angestoßen')).toBeVisible();
  });

  test('purge stays disabled until the channel name is typed verbatim, then deletes and reloads', async ({
    page,
  }) => {
    await mockAdminChannelList(page, [
      { channelName: 'handofblood', emoteCount: 903 },
      { channelName: 'sensitron' },
    ]);
    await mockPurge(page, 'handofblood');

    await page.goto('/admin/channels');

    await page
      .getByRole('listitem')
      .filter({ hasText: '#handofblood' })
      .getByRole('button', { name: 'Löschen' })
      .click();

    // Scoped to the dialog throughout: the row behind it shows the same emote count, and an
    // unscoped locator would happily pass on the wrong element.
    const dialog = page.getByRole('dialog');
    // The dialog names what the cascade takes with it — emotes, usage stats, sessions, votes.
    await expect(
      dialog.getByRole('heading', { name: 'Channel unwiderruflich löschen' }),
    ).toBeVisible();
    await expect(dialog.getByText('903 Emotes')).toBeVisible();

    const confirmButton = dialog.getByRole('button', { name: 'Endgültig löschen' });
    const input = dialog.getByLabel('Channel-Name zur Bestätigung');

    await expect(confirmButton).toBeDisabled();

    // A near-miss must not unlock it — that is the entire point over a plain yes/no confirm.
    await input.fill('handofbloo');
    await expect(confirmButton).toBeDisabled();
    await input.fill('HandOfBlood');
    await expect(confirmButton).toBeDisabled();

    await input.fill('handofblood');
    await expect(confirmButton).toBeEnabled();

    // Re-registered so the reload after the purge answers without the deleted channel; Playwright
    // matches route handlers in reverse registration order, so this one wins from here on.
    await mockAdminChannelList(page, [{ channelName: 'sensitron' }]);

    const purgeRequest = page.waitForRequest(
      (request) =>
        request.url().includes('/api/channels/handofblood/purge') && request.method() === 'DELETE',
    );
    await confirmButton.click();
    await purgeRequest;

    // Row gone = the list actually reloaded rather than only the dialog closing.
    await expect(page.getByRole('link', { name: '#handofblood' })).toHaveCount(0);
    await expect(page.getByRole('link', { name: '#sensitron' })).toBeVisible();
  });
});

test.describe('non-admin', () => {
  test('is redirected from /admin to the overview', async ({ page }) => {
    await mockAuthMe(page, AUTH_USER); // isGlobalAdmin: false
    await mockWorkerHealth(page, 'connected');
    await mockMyChannels(page, []);

    await page.goto('/admin/channels');

    // adminGuard sends them to '/' rather than '/login': the session is fine, the role is not.
    await expect(page).toHaveURL('/');
    await expect(page.getByRole('link', { name: 'Admin' })).toHaveCount(0);
  });
});
