/**
 * The usage-stats action dock is not one block: it renders a *marking* half (the marked count, the
 * mass-delete panel and the vote-session button projected into it) and an *import* half
 * (`app-import-progress-section`), and each carries its own gate in the template. The marking half
 * needs this channel to have an active 7TV set — there is nothing to mark slots against without
 * one — while the import half deliberately does not, because a copy run writes into *another*
 * channel's set and has to stay visible, Cancel button included, on every usage-stats page it is
 * opened from (R9).
 *
 * Mounting the bar on "something is selected" alone therefore has an empty state: a transient 5xx on
 * `GET …/emote-set` leaves the grid standing with no set status, and the first click on a cell then
 * produced an accent-framed bar with nothing in it, plus the `pb-40` of empty space the page keeps
 * free for it. This function is what the bar and that padding are bound to instead.
 *
 * Pure rather than inlined in the page's `computed`, so the empty-bar case can be pinned by a test:
 * Regel 12 rules out an isolated component test for the page itself.
 */
export interface ActionDockState {
  /** This channel has an active 7TV set — without one the marking half renders nothing. */
  readonly hasActiveSet: boolean;
  readonly markedCount: number;
  /** A delete run is in flight or settled-but-still-shown (its protocol download lives there). */
  readonly deleteShown: boolean;
  /** A restore run is in flight or settled-but-still-shown. */
  readonly restoreShown: boolean;
  /** An import run is in flight or settled-but-still-shown. Independent of `hasActiveSet` on
   *  purpose — see above. */
  readonly importShown: boolean;
}

export function actionDockHasContent(state: ActionDockState): boolean {
  const markingShown = state.markedCount > 0 || state.deleteShown || state.restoreShown;
  return (state.hasActiveSet && markingShown) || state.importShown;
}
