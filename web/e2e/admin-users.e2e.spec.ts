import { expect, test } from '@playwright/test';

import {
  AUTH_USER,
  mockAdminUsers,
  mockAuthMe,
  mockRevokeSessions,
  mockWorkerHealth,
} from './support/mocks';

const ADMIN_USER = { ...AUTH_USER, isGlobalAdmin: true };

test.describe('global admin on /admin/users', () => {
  test.beforeEach(async ({ page }) => {
    await mockAuthMe(page, ADMIN_USER);
    await mockWorkerHealth(page, 'connected');
  });

  test('sees every user with derived token status and metadata', async ({ page }) => {
    await mockAdminUsers(page, [
      {
        twitchUserId: '4711',
        twitchUsername: 'handofblood',
        displayName: 'HandOfBlood',
        hasRefreshToken: true,
        twitchAccessTokenExpiresAtUtc: '2026-07-31T16:00:00Z',
        twitchTokenScopes: 'user:read:email',
      },
      {
        twitchUserId: '4712',
        twitchUsername: 'zweitaccount',
        displayName: 'Zweitaccount',
        hasRefreshToken: false,
        sessionsValidFromUtc: '2026-07-30T10:00:00Z',
      },
    ]);

    await page.goto('/admin/users');

    await expect(page.getByText('HandOfBlood')).toBeVisible();
    await expect(page.getByText('Twitch verbunden')).toBeVisible();
    await expect(page.getByText('Keine Tokens')).toBeVisible();
    await expect(page.getByText('Scopes: user:read:email')).toBeVisible();
    await expect(page.getByText('Twitch-ID 4711')).toBeVisible();
    // A user whose sessions were revoked shows the cutoff, so the admin sees the action took.
    await expect(page.getByText(/Sessions widerrufen am/)).toBeVisible();
  });

  test('revoke asks for confirmation, POSTs, and reloads the list', async ({ page }) => {
    await mockAdminUsers(page, [
      { twitchUserId: '4712', twitchUsername: 'zweitaccount', displayName: 'Zweitaccount' },
    ]);
    await mockRevokeSessions(page, '4712');

    await page.goto('/admin/users');
    await page.getByRole('button', { name: 'Sessions widerrufen' }).click();

    // The confirm dialog names the target user; nothing is sent before confirmation.
    await expect(page.getByText(/Alle Sessions von Zweitaccount widerrufen\?/)).toBeVisible();

    const revokeRequest = page.waitForRequest(
      (request) =>
        request.method() === 'POST' && request.url().includes('/api/admin/users/4712/revoke-sessions'),
    );
    const reloadRequest = page.waitForRequest(
      (request) => request.method() === 'GET' && request.url().includes('/api/admin/users?'),
    );
    await page.getByRole('dialog').getByRole('button', { name: 'Sessions widerrufen' }).click();
    await revokeRequest;
    await reloadRequest;

    await expect(page.getByRole('dialog')).toHaveCount(0);
  });

  test('cancelling the dialog sends nothing', async ({ page }) => {
    await mockAdminUsers(page, [
      { twitchUserId: '4712', twitchUsername: 'zweitaccount', displayName: 'Zweitaccount' },
    ]);
    let revokeCalled = false;
    await page.route('**/revoke-sessions', (route) => {
      revokeCalled = true;
      return route.fulfill({ status: 204 });
    });

    await page.goto('/admin/users');
    await page.getByRole('button', { name: 'Sessions widerrufen' }).click();
    await page.getByRole('button', { name: 'Abbrechen' }).click();

    await expect(page.getByRole('dialog')).toHaveCount(0);
    expect(revokeCalled).toBe(false);
  });
});
