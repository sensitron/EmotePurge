/**
 * The transloco key for the bot-exclusion sentence in the usage-statistics caption, or `null` when
 * it must stay silent — no bot has ever been seen in this channel, so there is nothing to explain.
 *
 * This is the ONE place the visibility rule lives, and deliberately just this one condition. It
 * would be tempting to also suppress the sentence when `botsExcludedSince` predates the channel's
 * `trackedSince` (joined only after the cutover, so — one might think — there is nothing earlier to
 * warn about). That temptation is wrong and is left out on purpose: `botsExcludedSince` is the day a
 * bot was first *seen*, not the day this feature started counting bot usage apart for this channel —
 * the real cutover is a deploy event, not a data event, and a row written before it is
 * indistinguishable from one written after once `BotUseCount` happens to be 0. A channel joined on
 * 2026-09-10 whose first bot was seen on 2026-09-11 has nothing but clean rows, yet the
 * earliest-sighting date is still after the join date — a `trackedSince` comparison would not catch
 * that case at all, and the sentence would go on claiming mixed numbers for the exact channel it was
 * trying to protect. There is no discriminator for "the cutover happened here" in the data, so none
 * is coded here. The resulting error direction — occasionally naming a date for numbers that are
 * actually clean throughout — is the conservative one, not a bug to route around. See
 * docs/DECISIONS.md, 2026-09-01 ("Bot-Nutzung bekommt eine zweite Spalte, keine zweite Zeile"), E4.
 */
export function botsExcludedCaptionKey(botsExcludedSince: string | null): string | null {
  return botsExcludedSince ? 'usageStats.botsExcludedSince' : null;
}
