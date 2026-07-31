import * as fs from 'node:fs';
import * as path from 'node:path';

import { Page, test } from '@playwright/test';

import {
  AUTH_USER,
  MockChannel,
  mockActiveEmoteSet,
  mockAdminChannelList,
  mockAdminHealth,
  mockAuditLog,
  mockAuthMe,
  mockChannelPermissions,
  mockChannelStatus,
  mockUsageTotals,
  mockWorkerHealth,
} from '../support/mocks';

// ---------------------------------------------------------------------------
// UI/UX-audit harness: screenshots every route in 3 viewports, both locales
// and edge states (empty/error/long names), plus JSON metrics on horizontal
// overflow and touch-target sizes. Not part of the regular e2e suite — run on
// demand via playwright.audit.config.ts. Output: web/.audit-out/ (gitignored).
// ---------------------------------------------------------------------------

const OUT = path.resolve(__dirname, '../../.audit-out');

const VIEWPORTS = [
  { name: 'mobile', width: 360, height: 800 },
  { name: 'tablet', width: 768, height: 1024 },
  { name: 'desktop', width: 1280, height: 900 },
] as const;

// 1x1 PNG stub so emote images from cdn.7tv.app never hit the network.
const PNG_1X1 = Buffer.from(
  'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==',
  'base64',
);

async function stubSevenTvCdn(page: Page): Promise<void> {
  await page.route('**cdn.7tv.app/**', (route) =>
    route.fulfill({ status: 200, contentType: 'image/png', body: PNG_1X1 }),
  );
}

interface MyChannelsFlags {
  helixUnavailable?: boolean;
  reauthRequired?: boolean;
  sevenTvUnavailable?: boolean;
}

async function mockMyChannelsWithFlags(
  page: Page,
  channels: MockChannel[],
  flags: MyChannelsFlags = {},
): Promise<void> {
  await page.route('**/api/channels/mine', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        helixUnavailable: flags.helixUnavailable ?? false,
        reauthRequired: flags.reauthRequired ?? false,
        sevenTvUnavailable: flags.sevenTvUnavailable ?? false,
        channels: channels.map((c) => ({
          channelName: c.channelName,
          isBroadcaster: c.isBroadcaster ?? false,
          isModerator: c.isModerator ?? false,
          isSevenTvEditor: c.isSevenTvEditor ?? false,
          isTracked: c.isTracked ?? false,
          isBotActive: c.isBotActive ?? false,
        })),
      }),
    }),
  );
}

function json(route: Parameters<Parameters<Page['route']>[1]>[0], status: number, body: unknown) {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

// --- data builders ---------------------------------------------------------

const LONG_EMOTE = 'xXSuperMegaLangesEmoteMitVielZuLangemNamenXx';

function usageEmotes(count: number) {
  return Array.from({ length: count }, (_, i) => ({
    emoteId: `e${i + 1}`,
    emoteName: i === 1 ? LONG_EMOTE : i === 5 ? 'catJAMJAMJAMJAMJAMJAM' : `Emote${i + 1}PogU`,
    sevenTvEmoteId: `7tv-${i + 1}`,
    imageUrl: 'https://cdn.7tv.app/emote/stub/1x.webp',
    totalUseCount: Math.max(1, Math.round(98765 / (i + 1))),
  }));
}

function voteSummaries(count: number) {
  return Array.from({ length: count }, (_, i) => ({
    id: i + 1,
    title:
      i === 0
        ? 'Frühjahrsputz 2026 — welche Emotes fliegen endgültig raus? (Community-Abstimmung, Runde 2)'
        : `Emote-Voting Runde ${i + 1}`,
    allowedVoterRoles: i % 2 === 0 ? 1 : 8,
    isActive: i < 3,
    startedAt: '2026-07-01T12:00:00Z',
    endedAt: i < 3 ? null : '2026-07-10T12:00:00Z',
  }));
}

function voteResults(sessionId: number, isActive: boolean) {
  return {
    sessionId,
    title:
      'Frühjahrsputz 2026 — welche Emotes fliegen endgültig raus? (Community-Abstimmung, Runde 2)',
    isActive,
    startedAt: '2026-07-01T12:00:00Z',
    endedAt: isActive ? null : '2026-07-10T12:00:00Z',
    emotes: usageEmotes(12).map((e, i) => ({
      ...e,
      normalizedUsageScore: Math.max(0, 1 - i * 0.09),
      keepVotes: 40 - i * 3,
      deleteVotes: 2 + i * 4,
      score: 1 - i * 0.15,
      myVote: i % 3 === 0 ? 1 : i % 3 === 1 ? 2 : null,
    })),
  };
}

function myVoteSessions(count: number) {
  return Array.from({ length: count }, (_, i) => ({
    sessionId: i + 1,
    title:
      i === 0
        ? 'Frühjahrsputz 2026 — welche Emotes fliegen endgültig raus? (Runde 2)'
        : `Voting Runde ${i + 1}`,
    channelName: i % 2 === 0 ? 'sensitron' : 'superlangertwitchchannelx',
    isActive: i < 2,
    startedAt: '2026-07-01T12:00:00Z',
    endedAt: i < 2 ? null : '2026-07-08T09:30:00Z',
    lastVotedAt: '2026-07-05T18:45:00Z',
  }));
}

// --- per-page mock bundles -------------------------------------------------

async function authedShell(page: Page): Promise<void> {
  await mockAuthMe(page, AUTH_USER);
  await mockWorkerHealth(page, 'connected');
}

/** Same shell, but the session is a global admin — unlocks the /admin area and its nav entry. */
async function adminShell(page: Page): Promise<void> {
  await mockAuthMe(page, { ...AUTH_USER, isGlobalAdmin: true });
  await mockWorkerHealth(page, 'connected');
}

async function channelWorkspace(
  page: Page,
  overrides: Parameters<typeof mockChannelPermissions>[2] = {},
): Promise<void> {
  await mockChannelPermissions(page, 'sensitron', overrides);
  await mockChannelStatus(page, 'sensitron');
  await mockActiveEmoteSet(page, 'sensitron');
  await page.route(
    (url) => url.pathname === '/api/channels/sensitron/emotes/set-warning',
    (route) =>
      json(route, 200, {
        available: true,
        isOwnSet: true,
        otherTrackedChannelsSharingSet: [],
        otherModeratedChannelsSharingSet: [],
      }),
  );
}

function mockVoteList(page: Page, sessions: ReturnType<typeof voteSummaries>): Promise<void> {
  return page.route(
    (url) => url.pathname === '/api/channels/sensitron/vote-sessions',
    (route) => {
      const url = new URL(route.request().url());
      const p = Number(url.searchParams.get('page') ?? '1');
      const ps = Number(url.searchParams.get('pageSize') ?? '20');
      return json(route, 200, {
        items: sessions.slice((p - 1) * ps, p * ps),
        page: p,
        pageSize: ps,
        totalCount: sessions.length,
        totalPages: Math.max(1, Math.ceil(sessions.length / ps)),
      });
    },
  );
}

function mockVoteResults(page: Page, sessionId: number, isActive: boolean): Promise<void> {
  return page.route(
    (url) => url.pathname === `/api/channels/sensitron/vote-sessions/${sessionId}/results`,
    (route) => json(route, 200, voteResults(sessionId, isActive)),
  );
}

function mockMyVotings(page: Page, sessions: ReturnType<typeof myVoteSessions>): Promise<void> {
  return page.route(
    (url) => url.pathname === '/api/vote-sessions/mine',
    (route) => {
      const url = new URL(route.request().url());
      const p = Number(url.searchParams.get('page') ?? '1');
      const ps = Number(url.searchParams.get('pageSize') ?? '10');
      return json(route, 200, {
        items: sessions.slice((p - 1) * ps, p * ps),
        page: p,
        pageSize: ps,
        totalCount: sessions.length,
        totalPages: Math.max(1, Math.ceil(sessions.length / ps)),
      });
    },
  );
}

// --- scenarios -------------------------------------------------------------

const TYPICAL_CHANNELS: MockChannel[] = [
  { channelName: 'sensitron', isBroadcaster: true, isTracked: true, isBotActive: true },
  { channelName: 'handofblood', isModerator: true, isTracked: true, isBotActive: true },
  {
    channelName: 'superlangertwitchchannelx',
    isModerator: true,
    isTracked: true,
    isBotActive: false,
  },
  { channelName: 'untrackedbuddy', isSevenTvEditor: true, isTracked: false, isBotActive: false },
];

interface Scenario {
  slug: string;
  path: string;
  setup: (page: Page) => Promise<void>;
}

const SCENARIOS: Scenario[] = [
  {
    slug: 'welcome',
    path: '/welcome',
    setup: async (page) => {
      await mockAuthMe(page, null);
    },
  },
  {
    slug: 'login',
    path: '/login',
    setup: async (page) => {
      await mockAuthMe(page, null);
    },
  },
  {
    slug: 'overview-typical',
    path: '/',
    setup: async (page) => {
      await authedShell(page);
      await mockMyChannelsWithFlags(page, TYPICAL_CHANNELS);
    },
  },
  {
    slug: 'overview-empty',
    path: '/',
    setup: async (page) => {
      await authedShell(page);
      await mockMyChannelsWithFlags(page, []);
    },
  },
  {
    slug: 'overview-reauth',
    path: '/',
    setup: async (page) => {
      await authedShell(page);
      await mockMyChannelsWithFlags(page, TYPICAL_CHANNELS, { reauthRequired: true });
    },
  },
  {
    slug: 'overview-helix-down',
    path: '/',
    setup: async (page) => {
      await authedShell(page);
      await mockMyChannelsWithFlags(page, [], { helixUnavailable: true });
    },
  },
  {
    slug: 'overview-worker-down',
    path: '/',
    setup: async (page) => {
      await mockAuthMe(page, AUTH_USER);
      await mockWorkerHealth(page, 'disconnected');
      await mockMyChannelsWithFlags(page, TYPICAL_CHANNELS);
    },
  },
  // The global channel list moved out of the overview into /admin/channels — these three replace the
  // former `overview-admin` scenario.
  {
    slug: 'admin-monitoring',
    path: '/admin/monitoring',
    setup: async (page) => {
      await adminShell(page);
      await mockAdminHealth(page);
    },
  },
  {
    slug: 'admin-monitoring-degraded',
    path: '/admin/monitoring',
    setup: async (page) => {
      await adminShell(page);
      await mockAdminHealth(page, {
        status: 'stale',
        isConnected: false,
        secondsSinceLastMessage: 4212,
        sevenTv: { status: 'disconnected', connected: false, unacknowledgedCount: 3 },
        flush: { consecutiveFailures: 5, pendingEmoteCount: 1843 },
      });
    },
  },
  {
    slug: 'admin-channels',
    path: '/admin/channels',
    setup: async (page) => {
      await adminShell(page);
      await mockAdminChannelList(
        page,
        Array.from({ length: 10 }, (_, i) => ({
          channelName: i === 3 ? 'superlangertwitchchannelx' : `channel${i + 1}`,
          isBotActive: i % 3 !== 0,
          emoteCount: 87 * (i + 1),
          archivedEmoteCount: 3 * i,
          activeVoteSessionCount: i % 4 === 0 ? 1 : 0,
          voteSessionCount: i % 2,
          lastSyncedAtUtc: i === 5 ? null : '2026-07-31T11:45:00Z',
        })),
      );
    },
  },
  {
    slug: 'admin-channels-empty',
    path: '/admin/channels',
    setup: async (page) => {
      await adminShell(page);
      await mockAdminChannelList(page, []);
    },
  },
  {
    slug: 'admin-audit-log',
    path: '/admin/audit-log',
    setup: async (page) => {
      await adminShell(page);
      await mockAuditLog(
        page,
        {
          1: Array.from({ length: 25 }, (_, i) => ({
            id: 100 - i,
            action: ['channel.join', 'channel.leave', 'channel.purge', 'vote-session.create'][
              i % 4
            ],
            channelName: i % 3 === 0 ? 'superlangertwitchchannelx' : 'sensitron',
            targetType: i % 4 === 3 ? 'VoteSession' : 'Channel',
            targetId: String(i + 1),
          })),
        },
        63,
      );
    },
  },
  {
    slug: 'my-votings-list',
    path: '/my-votings',
    setup: async (page) => {
      await authedShell(page);
      await mockMyVotings(page, myVoteSessions(23));
    },
  },
  {
    slug: 'my-votings-empty',
    path: '/my-votings',
    setup: async (page) => {
      await authedShell(page);
      await mockMyVotings(page, []);
    },
  },
  {
    slug: 'usage-stats-grid',
    path: '/channels/sensitron/usage-stats',
    setup: async (page) => {
      await authedShell(page);
      await channelWorkspace(page);
      await mockUsageTotals(page, 'sensitron', usageEmotes(24));
    },
  },
  {
    slug: 'usage-stats-empty',
    path: '/channels/sensitron/usage-stats',
    setup: async (page) => {
      await authedShell(page);
      await channelWorkspace(page);
      await mockUsageTotals(page, 'sensitron', []);
    },
  },
  {
    slug: 'usage-stats-error',
    path: '/channels/sensitron/usage-stats',
    setup: async (page) => {
      await authedShell(page);
      await channelWorkspace(page);
      await page.route(
        (url) => url.pathname === '/api/channels/sensitron/usage-stats/totals',
        (route) => json(route, 500, { error: 'boom' }),
      );
    },
  },
  {
    slug: 'usage-stats-shared-set',
    path: '/channels/sensitron/usage-stats',
    setup: async (page) => {
      await authedShell(page);
      await mockChannelPermissions(page, 'sensitron');
      await mockChannelStatus(page, 'sensitron');
      await mockActiveEmoteSet(page, 'sensitron');
      await page.route(
        (url) => url.pathname === '/api/channels/sensitron/emotes/set-warning',
        (route) =>
          json(route, 200, {
            available: true,
            isOwnSet: false,
            otherTrackedChannelsSharingSet: ['handofblood', 'superlangertwitchchannelx'],
            otherModeratedChannelsSharingSet: ['handofblood'],
          }),
      );
      await mockUsageTotals(page, 'sensitron', usageEmotes(8));
    },
  },
  {
    slug: 'usage-stats-viewer-only',
    path: '/channels/sensitron/usage-stats',
    setup: async (page) => {
      await authedShell(page);
      await channelWorkspace(page, { canManage: false });
      await mockUsageTotals(page, 'sensitron', usageEmotes(8));
    },
  },
  {
    slug: 'vote-list',
    path: '/channels/sensitron/vote-sessions',
    setup: async (page) => {
      await authedShell(page);
      await channelWorkspace(page);
      await mockVoteList(page, voteSummaries(23));
    },
  },
  {
    slug: 'vote-list-empty',
    path: '/channels/sensitron/vote-sessions',
    setup: async (page) => {
      await authedShell(page);
      await channelWorkspace(page);
      await mockVoteList(page, []);
    },
  },
  {
    slug: 'vote-list-voter-only',
    path: '/channels/sensitron/vote-sessions',
    setup: async (page) => {
      await authedShell(page);
      await channelWorkspace(page, { canManage: false });
      await mockVoteList(page, voteSummaries(5));
    },
  },
  {
    slug: 'vote-detail-active',
    path: '/channels/sensitron/vote-sessions/5',
    setup: async (page) => {
      await authedShell(page);
      await channelWorkspace(page);
      await mockVoteResults(page, 5, true);
    },
  },
  {
    slug: 'vote-detail-ended',
    path: '/channels/sensitron/vote-sessions/5',
    setup: async (page) => {
      await authedShell(page);
      await channelWorkspace(page);
      await mockVoteResults(page, 5, false);
    },
  },
];

// --- metrics ---------------------------------------------------------------

async function collectMetrics(page: Page) {
  return page.evaluate(() => {
    const vw = window.innerWidth;
    const doc = document.documentElement;
    const interactive = Array.from(
      document.querySelectorAll<HTMLElement>(
        'a,button,input,select,textarea,[role="button"],[role="link"]',
      ),
    )
      .map((el) => {
        const r = el.getBoundingClientRect();
        return {
          tag: el.tagName.toLowerCase(),
          text: (el.getAttribute('aria-label') || el.textContent || '').trim().slice(0, 50),
          x: Math.round(r.x + window.scrollX),
          y: Math.round(r.y + window.scrollY),
          w: Math.round(r.width),
          h: Math.round(r.height),
        };
      })
      .filter((t) => t.w > 0 && t.h > 0);
    return {
      horizontalOverflowPx: Math.max(0, doc.scrollWidth - vw),
      smallTargetsUnder24: interactive.filter((t) => t.w < 24 || t.h < 24),
      targets24to43: interactive.filter((t) => (t.w < 44 || t.h < 44) && !(t.w < 24 || t.h < 24)),
      beyondRightEdge: interactive.filter((t) => t.x + t.w > vw + 1),
      interactiveCount: interactive.length,
    };
  });
}

// --- test matrix -----------------------------------------------------------

fs.mkdirSync(path.join(OUT, 'shots'), { recursive: true });
fs.mkdirSync(path.join(OUT, 'metrics'), { recursive: true });

for (const vp of VIEWPORTS) {
  for (const sc of SCENARIOS) {
    test(`${sc.slug} @ ${vp.name}`, async ({ page }, testInfo) => {
      const locale = testInfo.project.name;
      // English pass only in mobile (worst-case overflow) + desktop to keep the matrix sane.
      test.skip(locale === 'en' && vp.name === 'tablet', 'en only in mobile+desktop');

      await page.setViewportSize({ width: vp.width, height: vp.height });
      await stubSevenTvCdn(page);
      await sc.setup(page);
      await page.goto(sc.path);
      await page.waitForLoadState('networkidle');
      await page.waitForTimeout(400);

      const base = `${sc.slug}--${vp.name}--${locale}`;
      await page.screenshot({ path: path.join(OUT, 'shots', `${base}.png`), fullPage: true });
      const metrics = await collectMetrics(page);
      fs.writeFileSync(
        path.join(OUT, 'metrics', `${base}.json`),
        JSON.stringify(
          { scenario: sc.slug, viewport: vp.name, locale, url: sc.path, ...metrics },
          null,
          2,
        ),
      );
    });
  }
}
