import { Page } from '@playwright/test';

export const AUTH_USER = {
  twitchUserId: '1',
  login: 'sensitron',
  displayName: 'Sensitron',
  tokenExpiresAtUtc: '2099-01-01T00:00:00Z',
  // Non-admin by default — admin flows pass { ...AUTH_USER, isGlobalAdmin: true }.
  isGlobalAdmin: false,
};

async function fulfillJson(
  route: Parameters<Parameters<Page['route']>[1]>[0],
  status: number,
  body: unknown,
): Promise<void> {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

/** GET /api/auth/me — pass `null` to simulate a logged-out visitor (401). */
export async function mockAuthMe(page: Page, user: typeof AUTH_USER | null): Promise<void> {
  await page.route('**/api/auth/me', (route) =>
    user ? fulfillJson(route, 200, user) : route.fulfill({ status: 401 }),
  );
}

export async function mockWorkerHealth(
  page: Page,
  status: 'connected' | 'disconnected' | 'unknown' = 'unknown',
): Promise<void> {
  await page.route('**/api/worker/health', (route) => fulfillJson(route, 200, { status }));
}

export interface MockAdminHealth {
  snapshotAvailable: boolean;
  status: 'connected' | 'stale' | 'disconnected' | 'unknown';
  isConnected: boolean;
  lastMessageReceivedUtc: string | null;
  connectAttemptedUtc: string | null;
  secondsSinceLastMessage: number | null;
  sevenTv: {
    status: 'connected' | 'stale' | 'disconnected' | 'unknown' | 'disabled';
    enabled: boolean;
    connected: boolean;
    lastFrameUtc: string | null;
    lastDispatchUtc: string | null;
    connectAttemptedUtc: string | null;
    secondsSinceLastFrame: number | null;
    desiredChannelCount: number | null;
    desiredSubscriptionCount: number | null;
    unacknowledgedCount: number | null;
    subscriptionLimit: number;
  };
  flush: {
    consecutiveFailures: number | null;
    lastSuccessUtc: string | null;
    lastRowCount: number | null;
    pendingEmoteCount: number | null;
  };
}

/**
 * GET /api/admin/health — the admin monitoring page. Defaults to a fully healthy worker; pass
 * overrides for the degraded cases. `sevenTv`/`flush` are merged one level deep so a test can
 * override a single field without restating the whole sub-object.
 */
export async function mockAdminHealth(
  page: Page,
  overrides: Partial<Omit<MockAdminHealth, 'sevenTv' | 'flush'>> & {
    sevenTv?: Partial<MockAdminHealth['sevenTv']>;
    flush?: Partial<MockAdminHealth['flush']>;
  } = {},
): Promise<void> {
  const { sevenTv, flush, ...rest } = overrides;
  await page.route('**/api/admin/health', (route) =>
    fulfillJson(route, 200, {
      snapshotAvailable: true,
      status: 'connected',
      isConnected: true,
      lastMessageReceivedUtc: '2026-07-31T12:00:00Z',
      connectAttemptedUtc: '2026-07-31T11:00:00Z',
      secondsSinceLastMessage: 12,
      ...rest,
      sevenTv: {
        status: 'connected',
        enabled: true,
        connected: true,
        lastFrameUtc: '2026-07-31T12:00:00Z',
        lastDispatchUtc: '2026-07-31T11:59:00Z',
        connectAttemptedUtc: '2026-07-31T11:00:00Z',
        secondsSinceLastFrame: 5,
        desiredChannelCount: 7,
        desiredSubscriptionCount: 14,
        unacknowledgedCount: 0,
        subscriptionLimit: 500,
        ...sevenTv,
      },
      flush: {
        consecutiveFailures: 0,
        lastSuccessUtc: '2026-07-31T11:59:30Z',
        lastRowCount: 42,
        pendingEmoteCount: 3,
        ...flush,
      },
    }),
  );
}

/** GET/POST /api/auth/twitch/login — the real endpoint is a full OAuth redirect; here it just
 *  proves the browser actually navigated there, without needing a live Twitch app. */
export async function mockTwitchLoginRedirect(page: Page): Promise<void> {
  await page.route('**/api/auth/twitch/login', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'text/html',
      body: '<html><body>twitch login stub</body></html>',
    }),
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
      reauthRequired: false,
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

export interface MockAdminChannel {
  channelName: string;
  twitchChannelId?: string | null;
  isBotActive?: boolean;
  createdAt?: string;
  emoteCount?: number;
  archivedEmoteCount?: number;
  activeVoteSessionCount?: number;
  voteSessionCount?: number;
  lastSyncedAtUtc?: string | null;
}

/**
 * GET /api/admin/channels — the admin channel page's aggregate list, and since the overview's admin
 * section was removed the only global channel list in the app.
 */
export async function mockAdminChannelList(
  page: Page,
  channels: MockAdminChannel[],
): Promise<void> {
  await page.route('**/api/admin/channels', (route) =>
    fulfillJson(
      route,
      200,
      channels.map((c) => ({
        channelName: c.channelName,
        twitchChannelId: c.twitchChannelId ?? null,
        isBotActive: c.isBotActive ?? true,
        createdAt: c.createdAt ?? '2026-01-01T00:00:00Z',
        emoteCount: c.emoteCount ?? 0,
        archivedEmoteCount: c.archivedEmoteCount ?? 0,
        activeVoteSessionCount: c.activeVoteSessionCount ?? 0,
        voteSessionCount: c.voteSessionCount ?? 0,
        lastSyncedAtUtc: c.lastSyncedAtUtc ?? null,
      })),
    ),
  );
}

export interface MockAuditLogEntry {
  id: number;
  occurredAtUtc?: string;
  actorTwitchUserId?: string;
  actorLogin?: string;
  action: string;
  channelName?: string | null;
  targetType?: string | null;
  targetId?: string | null;
  detailsJson?: string | null;
}

/**
 * GET /api/admin/audit-log — the paged log. `entries` is keyed by page number, so one registration
 * can serve a pagination flow: the handler reads the `page` query parameter the app actually sent
 * and answers with that page's rows (empty array for a page the test did not define).
 *
 * `totalCount` defaults to enough rows to make the requested pages exist, because `totalPages` is
 * what decides whether the pager renders at all.
 *
 * When the request carries `action`/`channel`/`actor` filter params, the handler mirrors the
 * server's semantics over the union of all defined pages instead: action and channel match exactly
 * (channel case-insensitively — the server normalizes), actor is a case-insensitive substring, and
 * the filtered set is re-paged with its own total.
 */
export async function mockAuditLog(
  page: Page,
  entriesByPage: Record<number, MockAuditLogEntry[]>,
  totalCount?: number,
): Promise<void> {
  const pageSize = 25;
  const pageNumbers = Object.keys(entriesByPage).map(Number);
  const highestPage = pageNumbers.length > 0 ? Math.max(...pageNumbers) : 1;
  const effectiveTotal =
    totalCount ??
    (highestPage > 1
      ? (highestPage - 1) * pageSize + (entriesByPage[highestPage]?.length ?? 0)
      : (entriesByPage[1]?.length ?? 0));

  const withDefaults = (entry: MockAuditLogEntry) => ({
    id: entry.id,
    occurredAtUtc: entry.occurredAtUtc ?? '2026-07-31T12:00:00Z',
    actorTwitchUserId: entry.actorTwitchUserId ?? '1',
    actorLogin: entry.actorLogin ?? 'sensitron',
    action: entry.action,
    channelName: entry.channelName ?? null,
    targetType: entry.targetType ?? null,
    targetId: entry.targetId ?? null,
    detailsJson: entry.detailsJson ?? null,
  });

  await page.route('**/api/admin/audit-log**', (route) => {
    const params = new URL(route.request().url()).searchParams;
    const requestedPage = Number(params.get('page') ?? '1');
    const action = params.get('action');
    const channel = params.get('channel')?.trim().toLowerCase() || null;
    const actor = params.get('actor')?.trim().toLowerCase() || null;

    if (action || channel || actor) {
      const filtered = pageNumbers
        .sort((a, b) => a - b)
        .flatMap((pageNumber) => entriesByPage[pageNumber])
        .map(withDefaults)
        .filter(
          (entry) =>
            (!action || entry.action === action) &&
            (!channel || entry.channelName?.toLowerCase() === channel) &&
            (!actor || entry.actorLogin.toLowerCase().includes(actor)),
        );
      return fulfillJson(route, 200, {
        items: filtered.slice((requestedPage - 1) * pageSize, requestedPage * pageSize),
        page: requestedPage,
        pageSize,
        totalCount: filtered.length,
        totalPages: filtered.length === 0 ? 0 : Math.ceil(filtered.length / pageSize),
      });
    }

    const items = entriesByPage[requestedPage] ?? [];
    return fulfillJson(route, 200, {
      items: items.map(withDefaults),
      page: requestedPage,
      pageSize,
      totalCount: effectiveTotal,
      totalPages: effectiveTotal === 0 ? 0 : Math.ceil(effectiveTotal / pageSize),
    });
  });
}

/** DELETE /api/channels/{channelName}/purge — answers 204 like the real endpoint. Registered
 *  before mockChannelStatus-style routes would matter; the path suffix keeps it unambiguous. */
export async function mockPurge(page: Page, channelName: string): Promise<void> {
  await page.route(`**/api/channels/${channelName}/purge`, (route) =>
    route.fulfill({ status: 204 }),
  );
}

/** GET /api/channels/{channelName} — join status and the "join" response shape. */
export async function mockChannelStatus(
  page: Page,
  channelName: string,
  isBotActive = true,
): Promise<void> {
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
export async function mockUsageTotals(
  page: Page,
  channelName: string,
  emotes: MockEmoteUsage[],
): Promise<void> {
  await page.route(`**/api/channels/${channelName}/usage-stats/totals**`, (route) =>
    fulfillJson(route, 200, emotes),
  );
}

/** GET /api/channels/{channelName}/emotes/active-set — needed for the mass-delete panel to render. */
export async function mockActiveEmoteSet(
  page: Page,
  channelName: string,
  activeEmoteSetId = 'set-1',
): Promise<void> {
  await page.route(`**/api/channels/${channelName}/emotes/active-set`, (route) =>
    fulfillJson(route, 200, { activeEmoteSetId }),
  );
}
