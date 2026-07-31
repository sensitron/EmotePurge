/**
 * Client-side mirror of `EmotePurge.Core/Messaging/LiveEvents.cs` — the thin server→client push
 * envelope carried over SSE. Thin on purpose: the server never sends the payload itself, only "what
 * changed", and the client refetches through the ordinary REST endpoints (the vote read-model is
 * viewer- and role-specific, so broadcasting it would leak).
 *
 * There is no version field: the `type` string *is* the version. Unknown types are silently ignored
 * everywhere, which makes the deployment order between Api, Worker and this app irrelevant.
 */
export interface LiveEvent {
  type: string;
  channel?: string;
  sessionId?: number;
}

export const LIVE_EVENT_TYPES = {
  usageFlushed: 'usage.flushed',
  voteChanged: 'vote.changed',
  channelSynced: 'channel.synced',
  workerHealth: 'worker.health',
  /** Heartbeat, swallowed by LiveUpdateService — never reaches a consumer. */
  ping: 'ping',
} as const;

/** Admin-only stream; the group's GlobalAdminAuthorizationFilter guards it server-side. */
export const ADMIN_LIVE_URL = '/api/admin/live';

/** Per-channel stream. Requires a login only — the real authorization boundary is the refetch. */
export function channelLiveUrl(channelName: string): string {
  return `/api/channels/${channelName}/live`;
}
