/**
 * Whether the page state a channel-to-channel copy would capture — the active 7TV set id and the
 * loaded rows — actually describes `channelName`.
 *
 * A channel switch *inside* the usage-stats route (the import summary's "open target channel" link
 * is the one such navigation in the app) swaps `channelName()` immediately, while the set status and
 * the totals are only replaced once their own responses land. Both requests are in flight at the
 * same time and answer in either order, so there is a window in which the page shows the new
 * channel's name over the old channel's set id and rows. Capturing the push scope in that window
 * pairs channel B's name with channel A's emotes and A's `sourceEmoteSetId` — a wrong 7TV write into
 * a third set, and a saved emote-list file that misnames its own origin (#72).
 *
 * A plain "is it loading" flag does not close that window: the totals of the new channel can answer
 * before its set status does, at which point loading is over while `activeEmoteSetId()` is still the
 * previous channel's. Only naming the channel each half of the state belongs to catches both orders,
 * which is why the two arguments are channel names rather than booleans.
 */
export function importScopeIsCurrent(
  channelName: string,
  setStatusChannel: string | null,
  totalsChannel: string | null,
): boolean {
  return setStatusChannel === channelName && totalsChannel === channelName;
}
