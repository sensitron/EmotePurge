/**
 * The transloco key for the shared-chat sentence in the usage-statistics caption, or `null` when it
 * must stay silent — no mirrored message has ever been seen in this channel, so nothing was
 * separated away from its numbers and there is nothing to explain.
 *
 * The twin of {@link botsExcludedCaptionKey}, and deliberately just as narrow: the visibility rule
 * is "is there a date", nothing else. It is not compared against the channel's `trackedSince`, for
 * exactly the reason spelled out there — `sharedChatSeparatedSince` is the day shared chat was first
 * *seen*, not the day it started being counted apart, and there is no discriminator in the data for
 * the latter. A second, quiet condition would fire precisely where it is least tested.
 *
 * The reverse reading is the one to guard against: `null` never means "shared chat still counts
 * here". Since the separation shipped it is excluded in every channel; a `null` only says this
 * channel has no mirrored traffic on record. See docs/DECISIONS.md, 2026-09-08 ("Die Oberfläche
 * zeigt nur noch eigene Nutzung, die D5-Übergangssumme fällt").
 */
export function sharedChatSeparatedCaptionKey(
  sharedChatSeparatedSince: string | null,
): string | null {
  return sharedChatSeparatedSince ? 'usageStats.sharedChatSeparatedSince' : null;
}
