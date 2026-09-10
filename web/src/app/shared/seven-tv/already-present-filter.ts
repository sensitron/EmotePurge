import { Observable, catchError, map, of } from 'rxjs';

import { EmoteAdminService } from '../../core/emotes/emote-admin.service';

export interface AlreadyPresentFilterResult<T> {
  /** `rows` minus every entry already present in the target set. What the run should actually send —
   *  unfiltered (a copy of the input) when `available` is `false`. */
  rows: T[];
  /** How many entries were removed because they were already present. Always `0` when `available` is
   *  `false` — a failed check found nothing, it verified nothing. */
  skipped: number;
  /** Whether the check actually ran. `false` means the fetch failed: every row still passed through
   *  (see `rows`), but nobody verified it against the target set, so an undetected duplicate is
   *  possible. Same vocabulary as `EmoteSetWarning.available` in `emote-admin.service.ts` and the
   *  `UNAVAILABLE_WARNING` fallback in `import-target-loader.ts` — a failed check reads as "not
   *  verified", never as a false all-clear. */
  available: boolean;
}

/**
 * The one place that guards against #149's duplicate-push hole (docs/plans/Plan-149-7TV-v4-Schreibflaeche.md,
 * §0.6/T5): 7TV's `addEmote` mutation does not dedupe by emote id — it only rejects a colliding
 * *alias* string, otherwise it blindly appends. An emote already in the target set under a
 * *different* alias therefore gets pushed a second time, and a rollback cannot undo that; 7TV keeps
 * the duplicate. This became reachable once #149 moved the write surface to `v4`, whose alias
 * validator lets umlaut aliases through where `v3` used to reject them before the push ever ran.
 *
 * Used at the last moment before a run actually starts — both restore's two entry points
 * (`restore-flow.ts`, `mass-delete-panel.ts`) and import's (`import-flow.ts`) call this right in the
 * confirm-dialog-closed handler, immediately before handing rows to `startRestore`/`startImport` —
 * so the fetch it does is always fresh, never a dialog-open-time snapshot reused later. For import
 * this sits *on top of* `buildImportPreview`'s own dialog-time filter (`import-preview.ts`), not
 * instead of it: that filter can already be stale by the time the user actually confirms (another
 * editor, another tab, a long-open dialog), so this re-checks right before anything is sent.
 *
 * What this check can and cannot see: it asks our own API, which serves our database, not 7TV
 * live. That closes the dialog-snapshot gap — the rows are compared against something fetched
 * seconds ago rather than whenever the dialog opened — but it does not close the gap between our
 * mirror and 7TV itself, which is only ever as fresh as the last resync. An emote added to the set
 * at 7TV since then is invisible here.
 *
 * And even against a perfectly fresh view, a window remains between this check and each individual
 * `addEmote` call, in which another editor could write to the set. That race cannot be closed
 * without an atomic operation on 7TV's side, and this code does not attempt to close it.
 *
 * Fails open on a failed fetch — every row still passes through unfiltered, rather than blocking a
 * run the user already confirmed; a best-effort safety net, not a hard gate. What changed from the
 * first version of this filter is *only the reporting*: a failed fetch now sets `available: false`
 * instead of the indistinguishable `skipped: 0` a successful, nothing-to-skip check also produces.
 * That distinction is the rule `import-target-loader.ts` states explicitly for its own
 * `UNAVAILABLE_WARNING` fallback — "a failed check must read as 'not verified', never as a false
 * all-clear" — which this filter cited as precedent without actually following before this fix. It
 * matters more here than there: `UNAVAILABLE_WARNING` only silences an informational hint, but a
 * duplicate this filter misses is unrepairable (see the class doc above) — an unnoticed unguarded
 * run is worse than a visible one, so the failure has to reach the caller, not just the log.
 */
export function filterAlreadyPresent<T extends { sevenTvEmoteId: string }>(
  emoteAdminService: EmoteAdminService,
  channelName: string,
  rows: readonly T[],
): Observable<AlreadyPresentFilterResult<T>> {
  return emoteAdminService.listEmotes(channelName).pipe(
    map((targetEmotes) => {
      const targetIds = new Set(targetEmotes.map((emote) => emote.sevenTvEmoteId));
      const filtered = rows.filter((row) => !targetIds.has(row.sevenTvEmoteId));
      return { rows: filtered, skipped: rows.length - filtered.length, available: true };
    }),
    catchError(() => of({ rows: [...rows], skipped: 0, available: false })),
  );
}
