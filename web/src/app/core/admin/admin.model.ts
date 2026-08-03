import { ChannelLiveState } from '../channels/channel.model';

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
  /** 7TV's per-connection `subscription_limit`, taken from the last Hello frame when the worker has
   *  seen one and falling back to the documented 500 otherwise — so the utilization bar's
   *  denominator isn't hard-coded a second time here. */
  subscriptionLimit: number;
  /** How often the worker runs its full REST resync. Not a quota but a divisor: it is what turns
   *  "one request per channel" into a rate the page can state instead of imply. */
  resyncIntervalSeconds: number | null;
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
  worker: WorkerIdentity;
}

/** Which worker process wrote the snapshot, and since when it has been running. Counters reset on
 *  restart, so "0 failures" means something different in the first minute than after six hours. */
export interface WorkerIdentity {
  instanceId: string | null;
  processStartedUtc: string | null;
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
  /** When a full 7TV REST sync last completed, changed or not. This is the number that answers "is
   *  the sync running at all"; null means none has completed since the column exists. */
  lastSyncedAtUtc: string | null;
  /** When the emote inventory last actually moved. Deliberately separate from `lastSyncedAtUtc`: a
   *  healthy channel whose set nobody edits has a fresh sync and an ancient inventory change, and
   *  showing only the latter made that look like a stalled bot. Null when it has no emotes at all. */
  lastInventoryChangeUtc: string | null;
  /** Null when the channel has never synced — the server maps its empty-string default to null so
   *  the UI never renders a nameless set that exists. */
  activeEmoteSetId: string | null;
  activeEmoteSetCapacity: number | null;
  trackingResumedAt: string | null;
  /** From the worker's live-status snapshot, not the database — see `ChannelLiveState`. */
  liveState: ChannelLiveState;
}

/**
 * GET /api/admin/channels. The poll timestamp is snapshot-scoped (one Helix poll covered every
 * row), so it lives here and not on each channel.
 */
export interface AdminChannelsResult {
  channels: AdminChannel[];
  livePolledAtUtc: string | null;
}

/**
 * One channel as the worker itself currently sees it, from the roster snapshot it publishes to
 * Redis. This is the worker's in-memory truth, not the database's — comparing the two is the whole
 * point, so never merge the two shapes.
 */
export interface RosterChannel {
  channelName: string;
  ircJoinConfirmed: boolean;
  lastMessageUtc: string | null;
  /** Null means the worker holds no 7TV subscription intent for this channel at all. */
  sevenTvEmoteSetId: string | null;
  sevenTvEmoteSetAcknowledged: boolean;
  /** Null means no user subscription is desired, so `sevenTvUserAcknowledged: false` is a statement
   *  about nothing rather than a pending acknowledgement. */
  sevenTvUserId: string | null;
  sevenTvUserAcknowledged: boolean;
}

/** The ceilings the roster reports against. Two numbers on the Twitch side on purpose: one is
 *  Twitch's rule, the other is ours — the UI labels each with its provenance. */
export interface RosterCeilings {
  twitchConcurrentChannelLimit: number;
  twitchJoinBudgetChannels: number;
}

/**
 * GET /api/admin/roster — the worker's roster compared against the channels the database considers
 * active. Its own endpoint rather than more fields on /health, which every open monitoring page
 * refetches three times as often.
 *
 * Every field except `snapshotAvailable`, `trackedChannelCount` and `ceilings` is optional: when the
 * Redis key expired there is nothing to report, and inventing zeros would read as "the worker is up
 * and has joined nothing".
 */
export interface AdminRoster {
  snapshotAvailable: boolean;
  trackedChannelCount: number;
  ceilings: RosterCeilings;
  generatedAtUtc?: string;
  /** Staleness as a number, derived server-side — the TTL only ever says "gone", this says
   *  "present, and two minutes old". */
  ageSeconds?: number;
  workerInstanceId?: string;
  processStartedUtc?: string;
  /** False means the worker is still rejoining after a restart and every deficit below is expected.
   *  Without it a redeploy reads as a total outage for about a minute. */
  bootRecoveryCompleted?: boolean;
  truncated?: boolean;
  rosterChannelCount?: number;
  ircConfirmedCount?: number;
  sevenTvAcknowledgedCount?: number;
  /** Capped lists with their untruncated totals alongside — a short list standing in silently for a
   *  long one would read as "almost fine" on the page where that matters most. */
  missingFromIrc?: string[];
  missingFromIrcTotal?: number;
  missingFromSevenTv?: string[];
  missingFromSevenTvTotal?: number;
  /** The other direction: the worker holds a channel the database no longer considers active — what
   *  a leave that never reached the worker looks like. */
  unknownToDatabase?: string[];
  unknownToDatabaseTotal?: number;
}

/** GET /api/admin/channels/{channelName} — the database row and the worker's view of it, side by
 *  side rather than merged, so a disagreement between them is visible instead of resolved. */
export interface AdminChannelDetail {
  channel: AdminChannel;
  roster: {
    available: boolean;
    ageSeconds: number | null;
    bootRecoveryCompleted: boolean | null;
    workerInstanceId: string | null;
    /** Null while `available` is true is the finding, not a gap: the worker published a roster and
     *  this channel is not in it. */
    channel: RosterChannel | null;
  };
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
 * Optional narrowing of GET /api/admin/audit-log; fields are AND-combined server-side.
 * `action` matches exactly, `channel` matches the normalized name exactly (the server normalizes,
 * so raw input like "HandOfBlood" is fine), `actor` is a case-insensitive substring match.
 */
export interface AuditLogFilter {
  action?: string;
  channel?: string;
  actor?: string;
}

// The row type itself lives in `core/audit/audit.model.ts`: both this endpoint and the
// channel-scoped one return it, and a channel manager's page must not import from the
// global-admin client.
