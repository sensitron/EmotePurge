import { HttpErrorResponse } from '@angular/common/http';
import { catchError, forkJoin, map, Observable, of } from 'rxjs';

import { EmoteAdminService, EmoteSetWarning } from './emote-admin.service';
import { EmoteListItem } from './emote-list-item.model';
import { SevenTvSyncFailureReason } from './seven-tv-sync-failure';

/**
 * The import confirm dialog's picture of its target channel — the merge of `getSetStatus`,
 * `getSetWarning` and `listEmotes` into one value. `loading` is the state the dialog opens with
 * (before the first emission ever arrives); `failed`/`no-set` are terminal until a retry.
 */
export type ImportTargetLoadState =
  | { status: 'loading' }
  | { status: 'failed' }
  | { status: 'no-set' }
  | {
      status: 'ready';
      setId: string;
      occupiedSlots: number;
      capacity: number | null;
      syncFailureReason: SevenTvSyncFailureReason | null;
      emotes: EmoteListItem[];
      warning: EmoteSetWarning;
    };

/** Same fallback shape `mass-delete-panel.ts` uses when its own set-warning check fails — a
 *  failed check must read as "not verified", never as a false all-clear or a false alarm. */
const UNAVAILABLE_WARNING: EmoteSetWarning = {
  available: false,
  isOwnSet: false,
  otherTrackedChannelsSharingSet: [],
  otherModeratedChannelsSharingSet: [],
};

/** Tags one of the two blocking requests (`getSetStatus`, `listEmotes`) with what its failure
 *  means, instead of letting the error propagate — that is what lets `forkJoin` below combine all
 *  three requests without ever aborting on the first rejection. */
type BlockingOutcome<T> = { kind: 'ok'; value: T } | { kind: 'no-set' } | { kind: 'failed' };

function fetchBlocking<T>(source$: Observable<T>): Observable<BlockingOutcome<T>> {
  return source$.pipe(
    map((value): BlockingOutcome<T> => ({ kind: 'ok', value })),
    catchError((error: unknown) => {
      const notFound = error instanceof HttpErrorResponse && error.status === 404;
      return of<BlockingOutcome<T>>(notFound ? { kind: 'no-set' } : { kind: 'failed' });
    }),
  );
}

/**
 * Loads everything the import confirm dialog needs about its target channel in one shot.
 *
 * Deliberately not a plain `forkJoin` over the three raw requests (the issue's original ask):
 * that would abort the whole load the moment any one of them errors, while the requirement is
 * that a failed `getSetWarning` alone must still leave the dialog usable ("check unavailable"),
 * whereas a failed `getSetStatus` or `listEmotes` must block it. The three requests therefore each
 * catch their own error and resolve to a tagged outcome — the observable returned here always
 * emits exactly once and never errors, so a bare `subscribe(state => ...)` on it is enough, and a
 * retry is just calling this function again (it holds no state of its own to reset first).
 *
 * A missing active set (`activeEmoteSetId === ''`, or either blocking request 404ing) and any
 * other failure of a blocking request both count against `no-set`/`failed` — when both occur at
 * once, `no-set` wins: a 404 is the more conclusive of the two statements.
 */
export function loadImportTarget(
  emoteAdminService: EmoteAdminService,
  channelName: string,
): Observable<ImportTargetLoadState> {
  const status$ = fetchBlocking(emoteAdminService.getSetStatus(channelName));
  const emotes$ = fetchBlocking(emoteAdminService.listEmotes(channelName));
  const warning$ = emoteAdminService
    .getSetWarning(channelName)
    .pipe(catchError(() => of(UNAVAILABLE_WARNING)));

  return forkJoin([status$, emotes$, warning$]).pipe(
    map(([status, emotes, warning]): ImportTargetLoadState => {
      if (status.kind === 'no-set' || emotes.kind === 'no-set') {
        return { status: 'no-set' };
      }
      if (status.kind === 'failed' || emotes.kind === 'failed') {
        return { status: 'failed' };
      }
      if (status.value.activeEmoteSetId === '') {
        return { status: 'no-set' };
      }
      return {
        status: 'ready',
        setId: status.value.activeEmoteSetId,
        occupiedSlots: status.value.occupiedSlots,
        capacity: status.value.capacity,
        syncFailureReason: status.value.syncFailureReason,
        emotes: emotes.value,
        warning,
      };
    }),
  );
}
