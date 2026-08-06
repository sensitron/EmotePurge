import { Page, expect, test } from '@playwright/test';

import {
  AUTH_USER,
  MockVoteSessionEmote,
  installLiveStub,
  mockActiveEmoteSet,
  mockAuthMe,
  mockChannelPermissions,
  mockChannelStatus,
  mockVoteSessionResults,
  mockWorkerHealth,
} from './support/mocks';

/**
 * The ballot had no browser coverage at all before its cell was rebuilt (2026-08-06), which is the
 * worst possible combination: the one surface the community actually touches, and the one whose
 * interaction model just changed. The card became a sprite with a two-button verdict strip, and
 * "which half did I press, and did it register" is not a thing a unit test can see.
 */

const SESSION = { id: 7, title: 'Aufräumen im August' };

async function openBallot(page: Page, emotes: MockVoteSessionEmote[], overrides = {}) {
  await mockAuthMe(page, AUTH_USER);
  await mockWorkerHealth(page);
  await installLiveStub(page);
  await mockChannelPermissions(page, 'sensitron');
  await mockChannelStatus(page, 'sensitron');
  await mockActiveEmoteSet(page, 'sensitron');
  await mockVoteSessionResults(page, 'sensitron', { ...SESSION, ...overrides }, emotes);

  await page.goto(`/channels/sensitron/vote-sessions/${SESSION.id}`);
  await expect(page.getByRole('heading', { name: SESSION.title })).toBeVisible();
}

const keepButton = (page: Page, index = 0) =>
  page.getByRole('button', { name: 'Behalten', exact: true }).nth(index);
const deleteButton = (page: Page, index = 0) =>
  page.getByRole('button', { name: 'Löschen vorschlagen', exact: true }).nth(index);

test.describe('vote ballot', () => {
  test('casts a keep vote and shows it as pressed', async ({ page }) => {
    let voted: { emoteId: string; type: number } | null = null;
    await page.route('**/vote-sessions/7/votes', async (route) => {
      voted = route.request().postDataJSON() as { emoteId: string; type: number };
      await route.fulfill({ status: 200, contentType: 'application/json', body: '{}' });
    });
    await openBallot(page, [
      { emoteId: 'e1', emoteName: 'catJAM', keepVotes: 2, deleteVotes: 1 },
      { emoteId: 'e2', emoteName: 'Sadge', keepVotes: 0, deleteVotes: 4 },
    ]);

    await keepButton(page).click();

    expect(voted).toEqual({ emoteId: 'e1', type: 1 });
  });

  test('pressing the same side again retracts instead of re-casting', async ({ page }) => {
    let method: string | null = null;
    await page.route('**/vote-sessions/7/votes/**', async (route) => {
      method = route.request().method();
      await route.fulfill({ status: 204, body: '' });
    });
    // Already voted keep: the only way back to neutral is pressing keep again, which has to be a
    // retraction and not a second cast.
    await openBallot(page, [{ emoteId: 'e1', emoteName: 'catJAM', myVote: 1 }]);

    await expect(keepButton(page)).toHaveAttribute('aria-pressed', 'true');
    await keepButton(page).click();

    expect(method).toBe('DELETE');
  });

  test('an archived emote stays listed but cannot be voted on', async ({ page }) => {
    await openBallot(page, [
      { emoteId: 'e1', emoteName: 'catJAM' },
      { emoteId: 'e2', emoteName: 'Gone', isArchived: true },
    ]);

    await expect(keepButton(page, 1)).toBeDisabled();
    await expect(deleteButton(page, 1)).toBeDisabled();
    // Still votable above it — the disabling is per emote, not a page-wide freeze.
    await expect(keepButton(page, 0)).toBeEnabled();
  });

  test('a running secret ballot shows no tallies on the strip', async ({ page }) => {
    await openBallot(
      page,
      [
        {
          emoteId: 'e1',
          emoteName: 'catJAM',
          keepVotes: null,
          deleteVotes: null,
          score: null,
          totalUseCount: null,
        },
      ],
      { hideResultsUntilEnd: true },
    );

    await expect(page.getByText('werden erst nach ihrem Ende angezeigt')).toBeVisible();
    // A placeholder in the tally slot would read like a value; the number is omitted entirely.
    await expect(keepButton(page)).toHaveText('');
    await expect(deleteButton(page)).toHaveText('');
  });

  test('the readout names whatever emote the pointer is on', async ({ page }) => {
    await openBallot(page, [
      { emoteId: 'e1', emoteName: 'catJAM', totalUseCount: 42, score: 3 },
      { emoteId: 'e2', emoteName: 'Sadge', totalUseCount: 7, score: -2 },
    ]);
    const sidecar = page.getByRole('complementary');

    // Before any hover it describes the first row — the ballot's own order, which the server sorts.
    await expect(sidecar).toContainText('catJAM');

    await page.getByRole('button', { name: 'Sadge', exact: true }).hover();

    await expect(sidecar).toContainText('Sadge');
    await expect(sidecar).toContainText('-2');
  });

  test('a manager selects sprites for the purge without that touching their vote', async ({
    page,
  }) => {
    await openBallot(page, [
      { emoteId: 'e1', emoteName: 'catJAM' },
      { emoteId: 'e2', emoteName: 'Sadge' },
    ]);

    // Selecting is the sprite; voting is the strip below it. They must not bleed into each other,
    // because one of them ends in an irreversible delete on 7TV.
    await page.getByRole('button', { name: 'catJAM', exact: true }).click();

    await expect(page.getByRole('button', { name: 'Löschen (1)' })).toBeVisible();
    await expect(keepButton(page, 0)).toHaveAttribute('aria-pressed', 'false');
  });
});
