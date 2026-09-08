/**
 * How many `usageFlushed` bursts are still allowed to trigger an active-set status refetch
 * (`GET /api/channels/{name}/emotes/active-set`), counted from each base status request `load()`
 * makes — a mount, a channel switch, or an explicit reload. Never from a plain range change,
 * which reuses the base value rather than fetching it again.
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
 * Call `shouldRefreshOn` once per `usageFlushed` burst (never for a `channelSynced` burst — that
 * is a separate, unconditional refresh path and must not spend a probe) and `reset()` wherever
 * `usage-stats-page.ts`'s own `requestedSetStatusFor` re-arms, which is exactly where a fresh base
 * value was just requested. That deliberately includes a manual reload: someone who re-fetches the
 * base value and still sees an empty field has the same claim to a follow-up window as a mount,
 * and the automatic traffic a reload buys is bounded by three — fewer requests than the deliberate
 * action that bought them.
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

  /** Re-arms the full probe budget. Call wherever a fresh base status request goes out — mount,
   *  channel switch or explicit reload — never on a plain range change. */
  reset(): void {
    this.remaining = SET_STATUS_FLUSH_PROBE_LIMIT;
  }
}
