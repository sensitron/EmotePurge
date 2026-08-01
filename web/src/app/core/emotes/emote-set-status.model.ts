/**
 * The channel's 7TV set as a slot budget, plus since when its usage data can be trusted.
 * Served by `GET /api/channels/{channel}/emotes/active-set`.
 */
export interface EmoteSetStatus {
  /** Empty while the first 7TV sync is still pending — the page polls on that. */
  activeEmoteSetId: string;

  /**
   * The set's slot limit as 7TV reports it, `null` when it reports none. Render no budget at all in
   * that case: 7TV subscribers get sets larger than 1000, so a hard-coded denominator would be
   * wrong in exactly the cases where the number matters most.
   */
  capacity: number | null;

  /** Counted from our own active (non-archived) emote rows — the same rows the grid renders. */
  occupiedSlots: number;

  /** ISO timestamp of the last join that (re)activated the channel, else its creation. */
  trackedSince: string;
}
