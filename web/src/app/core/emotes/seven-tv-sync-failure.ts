/**
 * Mirrors `src/EmotePurge.Core/Services/SevenTvSyncFailureReasons.cs` — the API sends only these
 * stable, language-neutral codes and never prose (Regel 7), so translation happens exactly once,
 * through {@link sevenTvSyncFailureKey}.
 *
 * Exported as a runtime list as well as a type so `seven-tv-sync-failure.spec.ts` can assert that
 * both locale files carry every reason. The step from the C# file to here stays discipline, the
 * same gap `api-error.ts` documents.
 */
export type SevenTvSyncFailureReason =
  | 'no_seventv_account'
  | 'no_active_emote_set'
  | 'seventv_unavailable'
  | 'seventv_response_unusable';

export const SEVEN_TV_SYNC_FAILURE_REASONS: readonly SevenTvSyncFailureReason[] = [
  'no_seventv_account',
  'no_active_emote_set',
  'seventv_unavailable',
  'seventv_response_unusable',
];

/**
 * Three lengths per reason, because the same fact is needed at three sizes and splitting them per
 * surface would let the wording drift: `title` and `hint` build the user's empty state, `short` is
 * the one-liner the admin list row and the drilldown banner carry.
 */
export function sevenTvSyncFailureKey(
  reason: SevenTvSyncFailureReason,
  part: 'title' | 'hint' | 'short',
): string {
  return `sevenTvSync.failure.${reason}.${part}`;
}
