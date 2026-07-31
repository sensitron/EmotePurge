/** Twitch IRC state, derived server-side from the worker's snapshot (thresholds live in the Api). */
export type WorkerConnectionStatus = 'connected' | 'stale' | 'disconnected' | 'unknown';

/** Same, for the 7TV EventAPI — plus `disabled`, which keeps a switched-off event path
 *  distinguishable from a broken one (the feature flag `SevenTv:EventApi:Enabled`). */
export type SevenTvConnectionStatus = WorkerConnectionStatus | 'disabled';

export interface SevenTvHealth {
  status: SevenTvConnectionStatus;
  enabled: boolean;
  connected: boolean;
  lastFrameUtc: string | null;
  lastDispatchUtc: string | null;
  connectAttemptedUtc: string | null;
  secondsSinceLastFrame: number | null;
  desiredChannelCount: number | null;
  desiredSubscriptionCount: number | null;
  unacknowledgedCount: number | null;
  /** 7TV's per-connection `subscription_limit` (500), sent along so the utilization bar's
   *  denominator isn't hard-coded a second time here. */
  subscriptionLimit: number;
}

export interface FlushHealth {
  consecutiveFailures: number | null;
  lastSuccessUtc: string | null;
  lastRowCount: number | null;
  pendingEmoteCount: number | null;
}

/**
 * GET /api/admin/health — the full worker snapshot, admin-only (Z1 health split; the public
 * GET /api/worker/health stays minimal). Every detail field is nullable because a snapshot written
 * by an older worker simply lacks it, and "unknown" must not render as zero.
 */
export interface AdminHealth {
  /** False when the Redis key expired or was never written — the worker is gone or wedged. */
  snapshotAvailable: boolean;
  status: WorkerConnectionStatus;
  isConnected: boolean;
  lastMessageReceivedUtc: string | null;
  connectAttemptedUtc: string | null;
  secondsSinceLastMessage: number | null;
  sevenTv: SevenTvHealth;
  flush: FlushHealth;
}

/**
 * One row of GET /api/admin/channels. Counts come in total/subset pairs — `emoteCount` and
 * `voteSessionCount` are the full counts, `archivedEmoteCount`/`activeVoteSessionCount` the subsets
 * — so the page renders "12 (3 archiviert)" without doing arithmetic the server didn't sanction.
 */
export interface AdminChannel {
  channelName: string;
  twitchChannelId: string | null;
  isBotActive: boolean;
  createdAt: string;
  emoteCount: number;
  archivedEmoteCount: number;
  activeVoteSessionCount: number;
  voteSessionCount: number;
  /** Newest `Emote.LastSyncedAt` of the channel; null when it has no emotes at all — which is a
   *  different statement than "synced a long time ago" and stays visible as such. */
  lastSyncedAtUtc: string | null;
}

/**
 * One row of GET /api/admin/users (paged). Token state arrives as derived status only —
 * `hasRefreshToken` is a boolean the server computes over the encrypted column; the ciphertexts
 * themselves never appear in any API response. `sessionsValidFromUtc` is the server-side revocation
 * cutoff (null = never revoked), shown so an admin can see a forced logout took effect.
 */
export interface AdminUser {
  twitchUserId: string;
  twitchUsername: string;
  displayName: string;
  lastLogin: string;
  sessionsValidFromUtc: string | null;
  hasRefreshToken: boolean;
  twitchAccessTokenExpiresAtUtc: string | null;
  twitchTokenScopes: string | null;
}

/**
 * The audited actions, mirroring `EmotePurge.Core.Entities.AuditActions`. Typed as a union of
 * the literal strings the server sends, but consumers must still handle an unknown value: an entry
 * written by a newer backend carries an action this build has no label for, and a log that hides
 * rows it cannot name would be worse than one that shows the raw string.
 */
export type AuditAction =
  | 'channel.join'
  | 'channel.leave'
  | 'channel.purge'
  | 'channel.resync'
  | 'voteSession.create'
  | 'voteSession.end'
  | 'voteSession.delete'
  | 'emotes.syncDeleted'
  | 'user.revokeSessions'
  | 'user.invalidateRoleCache';

/**
 * Optional narrowing of GET /api/admin/audit-log; fields are AND-combined server-side.
 * `action` matches exactly, `channel` matches the normalized name exactly (the server normalizes,
 * so raw input like "HandOfBlood" is fine), `actor` is a case-insensitive substring match.
 */
export interface AuditLogFilter {
  action?: string;
  channel?: string;
  actor?: string;
}

/**
 * One row of GET /api/admin/audit-log (paged via the shared `PagedResult<T>` envelope).
 *
 * `actorLogin` and `channelName` are snapshots taken when the action happened, not live joins — a
 * renamed account or a purged channel still shows what was true at the time, which is the point of
 * an audit log. `detailsJson` is raw JSON text, not a parsed object: its shape is per-action and
 * open-ended, so the page parses it defensively rather than the type pretending to know it.
 */
export interface AuditLogEntry {
  id: number;
  occurredAtUtc: string;
  actorTwitchUserId: string;
  actorLogin: string;
  action: AuditAction | string;
  channelName: string | null;
  targetType: string | null;
  targetId: string | null;
  detailsJson: string | null;
}
