import { expect, test } from '@playwright/test';

import { AUTH_USER, mockAuditLog, mockAuthMe, mockWorkerHealth } from './support/mocks';

// Its own file rather than an addition to admin-channels.e2e.spec.ts: that file's two tests share a
// channel-list fixture this page has no use for, and the audit log is a separate route with its own
// mock surface.
const ADMIN_USER = { ...AUTH_USER, isGlobalAdmin: true };

test.describe('global admin on /admin/audit-log', () => {
  test.beforeEach(async ({ page }) => {
    await mockAuthMe(page, ADMIN_USER);
    await mockWorkerHealth(page, 'connected');
  });

  test('sees entries with translated actions, actor, channel link and details', async ({
    page,
  }) => {
    await mockAuditLog(page, {
      1: [
        { id: 3, action: 'channel.purge', channelName: 'oldchannel', actorLogin: 'sensitron' },
        {
          id: 2,
          action: 'emotes.syncDeleted',
          channelName: 'handofblood',
          detailsJson: '{"emoteCount": 12}',
        },
        {
          id: 1,
          action: 'voteSession.create',
          channelName: 'handofblood',
          targetType: 'voteSession',
          targetId: '42',
          detailsJson: '{"title": "Sommer-Purge"}',
        },
      ],
    });

    await page.goto('/admin/audit-log');

    // The action string is never shown raw — every one of the seven has a label.
    await expect(page.getByText('Channel gelöscht')).toBeVisible();
    await expect(page.getByText('Emotes als gelöscht markiert')).toBeVisible();
    await expect(page.getByText('Abstimmung erstellt')).toBeVisible();

    // Actor and details come from the row itself; the channel is a link into its workspace.
    await expect(page.getByText('von sensitron').first()).toBeVisible();
    await expect(page.getByText('12 Emotes')).toBeVisible();
    await expect(page.getByText('„Sommer-Purge“')).toBeVisible();
    await expect(page.getByRole('link', { name: '#oldchannel' })).toHaveAttribute(
      'href',
      '/channels/oldchannel/usage-stats',
    );
  });

  test('pagination requests the next page and renders its rows', async ({ page }) => {
    await mockAuditLog(page, {
      1: [{ id: 30, action: 'channel.join', channelName: 'handofblood' }],
      2: [{ id: 5, action: 'channel.leave', channelName: 'sensitron' }],
    });

    await page.goto('/admin/audit-log');
    await expect(page.getByText('Channel beigetreten')).toBeVisible();

    const secondPageRequest = page.waitForRequest(
      (request) =>
        request.url().includes('/api/admin/audit-log') && request.url().includes('page=2'),
    );
    await page.getByRole('button', { name: 'Weiter' }).click();
    await secondPageRequest;

    await expect(page.getByText('Channel verlassen')).toBeVisible();
    await expect(page.getByText('Channel beigetreten')).toHaveCount(0);
  });

  test('renders the empty state when nothing has been audited yet', async ({ page }) => {
    await mockAuditLog(page, { 1: [] });

    await page.goto('/admin/audit-log');

    await expect(page.getByText('Noch keine Einträge.')).toBeVisible();
    // No pager for a single (empty) page — Pager renders nothing below totalPages 2.
    await expect(page.getByRole('button', { name: 'Weiter' })).toHaveCount(0);
  });
});
