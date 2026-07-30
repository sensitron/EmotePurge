import { Page } from '@playwright/test';

export const AUTH_USER = {
  twitchUserId: '1',
  login: 'sensitron',
  displayName: 'Sensitron',
  tokenExpiresAtUtc: '2099-01-01T00:00:00Z',
};

async function fulfillJson(route: Parameters<Parameters<Page['route']>[1]>[0], status: number, body: unknown): Promise<void> {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

/** GET /api/auth/me — pass `null` to simulate a logged-out visitor (401). */
export async function mockAuthMe(page: Page, user: typeof AUTH_USER | null): Promise<void> {
  await page.route('**/api/auth/me', (route) => (user ? fulfillJson(route, 200, user) : route.fulfill({ status: 401 })));
}

export async function mockWorkerHealth(page: Page, status: 'connected' | 'disconnected' | 'unknown' = 'unknown'): Promise<void> {
  await page.route('**/api/worker/health', (route) => fulfillJson(route, 200, { status }));
}

/** GET/POST /api/auth/twitch/login — the real endpoint is a full OAuth redirect; here it just
 *  proves the browser actually navigated there, without needing a live Twitch app. */
export async function mockTwitchLoginRedirect(page: Page): Promise<void> {
  await page.route('**/api/auth/twitch/login', (route) =>
    route.fulfill({ status: 200, contentType: 'text/html', body: '<html><body>twitch login stub</body></html>' }),
  );
}

export interface MockChannel {
  channelName: string;
  isBroadcaster?: boolean;
  isModerator?: boolean;
  isSevenTvEditor?: boolean;
  isTracked?: boolean;
  isBotActive?: boolean;
}

/** GET /api/channels/mine — the overview page's "Meine Channels" section. */
export async function mockMyChannels(page: Page, channels: MockChannel[]): Promise<void> {
  await page.route('**/api/channels/mine', (route) =>
    fulfillJson(route, 200, {
      helixUnavailable: false,
      sevenTvUnavailable: false,
      channels: channels.map((c) => ({
        channelName: c.channelName,
        isBroadcaster: c.isBroadcaster ?? false,
        isModerator: c.isModerator ?? false,
        isSevenTvEditor: c.isSevenTvEditor ?? false,
        isTracked: c.isTracked ?? false,
        isBotActive: c.isBotActive ?? false,
      })),
    }),
  );
}

/** GET /api/channels — admin-only global channel list; 403 for everyone else. */
export async function mockAdminChannels(page: Page, channels: MockChannel[] | 'forbidden'): Promise<void> {
  await page.route('**/api/channels', (route) => {
    if (channels === 'forbidden') {
      return route.fulfill({ status: 403 });
    }
    return fulfillJson(
      route,
      200,
      channels.map((c, i) => ({
        channelId: String(i + 1),
        channelName: c.channelName,
        isBotActive: c.isBotActive ?? false,
        twitchChannelId: null,
        createdAt: '2026-01-01T00:00:00Z',
      })),
    );
  });
}

/** GET /api/channels/{channelName} — join status and the "join" response shape. */
export async function mockChannelStatus(page: Page, channelName: string, isBotActive = true): Promise<void> {
  await page.route(`**/api/channels/${channelName}`, (route) => {
    if (route.request().method() !== 'GET') {
      return route.fallback();
    }
    return fulfillJson(route, 200, {
      channelId: '1',
      channelName,
      isBotActive,
      activeEmoteSetId: 'set-1',
    });
  });
}

/**
 * GET /api/channels/{channelName}/permissions — the single permission read behind
 * usageStatsAccessGuard, ChannelWorkspaceLayout and the vote-session list's "may manage" section.
 * Defaults to a full-access manager; pass overrides for the restricted cases.
 */
export async function mockChannelPermissions(
  page: Page,
  channelName: string,
  overrides: Partial<{
    canManage: boolean;
    canViewUsageStats: boolean;
    isGlobalAdmin: boolean;
    isTracked: boolean;
    isBotActive: boolean;
  }> = {},
): Promise<void> {
  await page.route(`**/api/channels/${channelName}/permissions`, (route) =>
    fulfillJson(route, 200, {
      canManage: true,
      canViewUsageStats: true,
      isGlobalAdmin: false,
      isTracked: true,
      isBotActive: true,
      ...overrides,
    }),
  );
}

export interface MockEmoteUsage {
  emoteId: string;
  emoteName: string;
  sevenTvEmoteId: string;
  imageUrl: string;
  totalUseCount: number;
}

/** GET /api/channels/{channelName}/usage-stats/totals — the usage-stats page's actual data. */
export async function mockUsageTotals(page: Page, channelName: string, emotes: MockEmoteUsage[]): Promise<void> {
  await page.route(`**/api/channels/${channelName}/usage-stats/totals**`, (route) => fulfillJson(route, 200, emotes));
}

/** GET /api/channels/{channelName}/emotes/active-set — needed for the mass-delete panel to render. */
export async function mockActiveEmoteSet(page: Page, channelName: string, activeEmoteSetId = 'set-1'): Promise<void> {
  await page.route(`**/api/channels/${channelName}/emotes/active-set`, (route) => fulfillJson(route, 200, { activeEmoteSetId }));
}
