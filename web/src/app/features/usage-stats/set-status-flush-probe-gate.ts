/**
 * How many `usageFlushed` bursts, counted from a mount or channel switch, are still allowed to
 * trigger an active-set status refetch (`GET /api/channels/{name}/emotes/active-set`).
 *
 * This replaces a staleness check that derived its answer from `botsExcludedSince` and
 * `sharedChatSeparatedSince` (both OR-linked, so either field alone being empty kept the check
 * open forever). `sharedChatSeparatedSince` stays `null` for any channel that never took part in a
 * Twitch shared-chat session — the common case — so most channels asked again after *every* flush,
 * unbounded: up to two extra GETs per minute for as long as the page stayed open.
 *
 * Three probes cover the worker's 30 s flush cadence (`UsageFlushWorker.cs`) for about 90 seconds —
 * the window a freshly tracked channel needs for the "bots excluded" / "shared chat separated"
 * captions to catch up without a manual reload. Both fields are `MIN` over a growing set of dates,
 * so once a field holds a date it is provably done changing; a channel that still has an empty
 * field after three probes is not going to answer sooner just because a fourth request tries.
 */
export const SET_STATUS_FLUSH_PROBE_LIMIT = 3;

/** The two fields the gate's shortcut looks at — a subset of `EmoteSetStatus`, kept narrow so the
 *  gate does not need to import the full model. */
export interface SetStatusCompletionFields {
  readonly botsExcludedSince: string | null;
  readonly sharedChatSeparatedSince: string | null;
}

/**
 * Pure per-channel decision: does *this* `usageFlushed` burst still earn a status refetch?
 *
 * One instance lives for as long as its channel is mounted. Call `shouldRefreshOn` once per
 * `usageFlushed` burst (never for a `channelSynced` burst — that is a separate, unconditional
 * refresh path and must not spend a probe) and `reset()` on every channel switch, mirroring how
 * `usage-stats-page.ts`'s own `requestedSetStatusFor` re-arms itself on a new channel.
 */
export class SetStatusFlushProbeGate {
  private remaining = SET_STATUS_FLUSH_PROBE_LIMIT;

  /**
   * Returns whether this burst should trigger `refreshSetStatus()`, and consumes one probe only
   * when it does. Both completion fields already present short-circuits to `false` without
   * touching the budget — there is nothing left to learn, so a later channel switch should not
   * find the budget already spent on requests that could not have changed anything.
   */
  shouldRefreshOn(status: SetStatusCompletionFields | null): boolean {
    if (status?.botsExcludedSince && status?.sharedChatSeparatedSince) {
      return false;
    }

    if (this.remaining <= 0) {
      return false;
    }

    this.remaining -= 1;
    return true;
  }

  /** Re-arms the full probe budget. Call on a channel switch, not on a plain range change. */
  reset(): void {
    this.remaining = SET_STATUS_FLUSH_PROBE_LIMIT;
  }
}
