import { HttpClient } from '@angular/common/http';
import { Observable, catchError, map, of, switchMap, throwError } from 'rxjs';

/** Host-absolute, same endpoint the write mutations already use (`seven-tv-run-engine.ts`) —
 *  reading a set's contents is public on `v4`, so unlike the mutations this needs no
 *  `Authorization` header and no 7TV token. */
const SEVEN_TV_GQL_ENDPOINT = 'https://7tv.io/v4/gql';

// Mirrors SevenTvApiClient.cs's GqlEmoteSetPreviewQuery/SetEntriesPerPage/MaxSetEntryPages
// (`src/EmotePurge.Infrastructure/SevenTv/SevenTvApiClient.cs`): 500 per page keeps even a
// subscriber-sized set (capacity can exceed 1000) at a handful of requests, and the 10-page cap is
// a runaway guard, not an expected limit — nothing in this codebase has ever seen a set anywhere
// near 5000 entries. Only `emote.id` is requested: unlike the backend's preview query (which also
// needs alias/name/scores for a human-facing list), this only ever compares ids.
const SET_ENTRIES_PER_PAGE = 500;
const MAX_SET_ENTRY_PAGES = 10;

const GQL_EMOTE_SET_IDS_QUERY =
  'query($id: Id!, $page: Int!, $perPage: Int!) { emoteSets { emoteSet(id: $id) { emotes(page: $page, perPage: $perPage) { totalCount pageCount items { emote { id } } } } } }';

interface SevenTvGqlEmoteSetIdsResponse {
  data?: {
    emoteSets?: {
      emoteSet?: {
        emotes?: {
          pageCount: number;
          items: { emote: { id: string } }[];
        } | null;
      } | null;
    } | null;
  };
  // 7TV can answer a GraphQL-level rejection (e.g. an unknown set id) with HTTP 200 — presence of
  // this array, regardless of content, is what `loadAllSevenTvEmoteIds` treats as "no usable data".
  errors?: unknown[];
}

function fetchEmoteSetIdsPage(
  httpClient: HttpClient,
  targetSetId: string,
  page: number,
): Observable<SevenTvGqlEmoteSetIdsResponse> {
  return httpClient.post<SevenTvGqlEmoteSetIdsResponse>(SEVEN_TV_GQL_ENDPOINT, {
    query: GQL_EMOTE_SET_IDS_QUERY,
    variables: { id: targetSetId, page, perPage: SET_ENTRIES_PER_PAGE },
  });
}

/** Walks every page of `targetSetId`'s current contents and collects the 7TV emote ids in it.
 *  Errors (network, HTTP, or a GraphQL-level rejection) all become a thrown error here — a single
 *  place for `filterAlreadyPresent`'s `catchError` below to fail open from, rather than each page
 *  reporting failure its own way. Stops early once a page reports it was the last one
 *  (`page >= pageCount`), and unconditionally at `MAX_SET_ENTRY_PAGES` — a set that size has never
 *  been seen in this codebase, so stopping there and using what was gathered so far mirrors the
 *  backend's own truncation behaviour (`GetEmoteSetPreviewAsync`) rather than failing the whole
 *  check over it. */
function loadAllSevenTvEmoteIds(
  httpClient: HttpClient,
  targetSetId: string,
): Observable<Set<string>> {
  const ids = new Set<string>();

  function loadPage(page: number): Observable<Set<string>> {
    return fetchEmoteSetIdsPage(httpClient, targetSetId, page).pipe(
      switchMap((response) => {
        const emotes = response.data?.emoteSets?.emoteSet?.emotes;
        if ((response.errors?.length ?? 0) > 0 || !emotes) {
          return throwError(() => new Error('7TV emote set read failed'));
        }
        for (const item of emotes.items) {
          ids.add(item.emote.id);
        }
        if (page >= emotes.pageCount || page >= MAX_SET_ENTRY_PAGES) {
          return of(ids);
        }
        return loadPage(page + 1);
      }),
    );
  }

  return loadPage(1);
}

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
 * What this check can and cannot see: it asks **7TV itself**, not our database — a P1 review finding
 * on the first version of this filter caught it asking `EmoteAdminService.listEmotes`, which serves
 * our Postgres mirror (`ListActiveAsync`, `!IsArchived`). That is wrong specifically for restore:
 * restore is the operation run *because* something already went wrong, most often right after a
 * delete whose closing `sync-deleted` report to our own backend is still pending, failed, or only
 * partially applied. In exactly that window our database still lists the just-deleted emotes as
 * active, while 7TV has already dropped them — so a check against our own API would misclassify the
 * rows the user is trying to restore as "already present", hand `startRestore` an empty or partial
 * queue, and silently not roll back the very thing the user is here to undo. Reading 7TV's live
 * `emoteSet` contents instead has no such staleness relative to our own mirror; it is the
 * authoritative source for what asking the alias-collision question is really about. Reading it also
 * costs nothing extra worth worrying about: this draws on 7TV's *global* rate-limit bucket
 * (5000/60s, HTTP-layer), not the far tighter `emote_set_change` bucket the mutations themselves
 * share — one read per run is negligible against it.
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
  httpClient: HttpClient,
  targetSetId: string,
  rows: readonly T[],
): Observable<AlreadyPresentFilterResult<T>> {
  return loadAllSevenTvEmoteIds(httpClient, targetSetId).pipe(
    map((targetIds) => {
      const filtered = rows.filter((row) => !targetIds.has(row.sevenTvEmoteId));
      return { rows: filtered, skipped: rows.length - filtered.length, available: true };
    }),
    catchError(() => of({ rows: [...rows], skipped: 0, available: false })),
  );
}
