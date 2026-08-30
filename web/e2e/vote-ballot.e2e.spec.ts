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
  // Issue #33 baseline flow (c): the guard's own authorization probe and the page's first
  // loadResults() used to each fetch /results independently, 582 ms apart. The guard now hands its
  // response to the page (VoteSessionService.stashGuardResults/takeGuardResults) instead.
  test('entering the page issues exactly one /results request', async ({ page }) => {
    let resultsRequests = 0;
    page.on('request', (request) => {
      if (new URL(request.url()).pathname.endsWith(`/vote-sessions/${SESSION.id}/results`)) {
        resultsRequests += 1;
      }
    });

    await openBallot(page, [{ emoteId: 'e1', emoteName: 'catJAM' }]);

    expect(resultsRequests).toBe(1);
  });

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

  test('sprite DOM nodes survive the reload a vote triggers (no rebuild-on-reload regression)', async ({
    page,
  }) => {
    // Needs multiple rows, not just multiple emotes: rows() hands *cdkVirtualFor a fresh row-array
    // reference on every recompute, so without a trackBy, CdkVirtualForOf's identity differ treats
    // every row as removed-then-inserted on each reload and its recycler reuses the detached row
    // views through a view cache — with 2+ rows that can rebind a recycled row-view to a *different*
    // row's data than it last held. The inner `@for (… track emote.emoteId)` then finds unfamiliar
    // ids in a view it did not expect them in and rebuilds every cell in it — a brand-new
    // `EmoteSprite` per cell, starting hidden until its own `load` event fires. 24 emotes reliably
    // fills more than one 10-14-column desktop row (see atlasColumns/CELL_WIDE_PX). Marking only one
    // sprite is not reliable here — which particular row-views get reshuffled depends on CDK's
    // internal cache order, and only about half of a 24-item grid's rows turned out to be affected
    // when this test was built (confirmed by temporarily reverting the trackBy: 10 of 24 marked
    // sprites survived, not 0) — so every sprite gets marked and every one of them must survive.
    const emotes = Array.from({ length: 24 }, (_, i) => ({
      emoteId: `e${i}`,
      emoteName: `Emote${i}`,
    }));
    await openBallot(page, emotes);

    // Scoped to the virtual-scroll viewport, and marked with a plain DOM attribute rather than
    // asserting on the component: a freshly created <img> never carries an attribute nobody bound
    // to it, so a drop in the marked count is unambiguous proof that elements were destroyed and
    // recreated, not just that their content changed.
    const sprites = page.locator('cdk-virtual-scroll-viewport img');
    const spriteCount = await sprites.count();
    expect(spriteCount).toBe(emotes.length);
    await sprites.evaluateAll((imgs) => {
      imgs.forEach((img, i) => img.setAttribute('data-regression-probe', String(i)));
    });

    await page.route('**/vote-sessions/7/votes', async (route) => {
      await route.fulfill({ status: 200, contentType: 'application/json', body: '{}' });
    });
    // Overrides the results route openBallot() already registered — Playwright tries the
    // most-recently-added matching handler first, so this wins for every request from here on,
    // including the one vote() fires. e0's tally is bumped to a value nothing else in this test
    // produces, purely so the assertion below has something visible to wait on.
    const votedEmotes = emotes.map((emote, i) => (i === 0 ? { ...emote, keepVotes: 999 } : emote));
    await mockVoteSessionResults(page, 'sensitron', SESSION, votedEmotes);

    await keepButton(page, 0).click();
    // Waiting on the GET's HTTP response is not enough: the response resolves before Angular has
    // applied it, and toHaveCount() below succeeds on its very first passing poll — which can land
    // in that gap, before a rebuild would even have happened, and pass for the wrong reason. This
    // waits on the reload's *rendered* effect instead (a tally only the post-vote response could
    // have produced), so everything after it is guaranteed to run against the post-reload DOM.
    await expect(keepButton(page, 0)).toContainText('999');

    await expect(page.locator('img[data-regression-probe]')).toHaveCount(spriteCount);
  });

  // Issue #33 baseline flow (d): four votes in a 14 ms window used to cost 2n+1 = 9 permits (4
  // mutations + 4 direct reloads + 1 SSE-echo reload) plus n policy-free channel-status reads.
  // vote() now feeds the same 500 ms reload pipeline the SSE echo does instead of reloading on its
  // own, so n votes should settle at n mutations + at most one reload, with the channel status
  // (loadActiveEmoteSetId) untouched by voting entirely.
  test('four fast votes cost four mutations and at most one results reload, no per-vote status recheck', async ({
    page,
  }) => {
    const emotes = Array.from({ length: 4 }, (_, i) => ({
      emoteId: `e${i}`,
      emoteName: `Emote${i}`,
    }));

    let voteRequests = 0;
    await page.route('**/vote-sessions/7/votes', async (route) => {
      voteRequests += 1;
      await route.fulfill({ status: 200, contentType: 'application/json', body: '{}' });
    });

    let resultsRequests = 0;
    let statusRequests = 0;
    page.on('request', (request) => {
      const path = new URL(request.url()).pathname;
      if (path.endsWith('/vote-sessions/7/results')) {
        resultsRequests += 1;
      }
      if (path === '/api/channels/sensitron') {
        statusRequests += 1;
      }
    });

    await openBallot(page, emotes);
    // openBallot's own mount already issues one /results (guard handoff) and one channel-status
    // read (loadActiveEmoteSetId) — not what this test is about, see the dedicated "exactly one
    // /results request" test above. Snapshot them instead of asserting on raw totals.
    const resultsAfterEntry = resultsRequests;
    const statusAfterEntry = statusRequests;

    // No installLiveStub echo is ever pushed in this test (no emitLive call) — the reload this test
    // observes can therefore only come from vote()'s own success handler, not from a Redis
    // publish/SSE round-trip, which is exactly the independence the spec requires.
    for (const index of emotes.keys()) {
      await keepButton(page, index).click();
    }

    expect(voteRequests).toBe(4);

    // Give the 500 ms debounce window (VOTE_RELOAD_DEBOUNCE_MS) time to fire and settle.
    await page.waitForTimeout(700);

    expect(resultsRequests - resultsAfterEntry).toBeGreaterThan(0);
    expect(resultsRequests - resultsAfterEntry).toBeLessThanOrEqual(1);
    expect(statusRequests).toBe(statusAfterEntry);
  });
});
