import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { inject, Injectable, signal } from '@angular/core';
import { TranslocoService } from '@jsverse/transloco';
import { retry, throwError, timer } from 'rxjs';

import { EmoteAdminService, SyncDeletedResult } from '../emotes/emote-admin.service';
import {
  RunItemStatus,
  RunOperation,
  RunQueueEmote,
  RunQueueItem,
  RunResult,
  RUN_DELAY_MS,
  SevenTvRunEngine,
} from './seven-tv-run-engine';
import { SevenTvTokenService } from './seven-tv-token.service';

/** Kept under its historical name — the engine's constant is the same value. */
export const DELETE_DELAY_MS = RUN_DELAY_MS;
// Exported for the restore service, which reports its run with the identical policy.
export const MAX_AUTOMATIC_SYNC_RETRIES = 2;
// Multiplied by the attempt number, so the two automatic attempts land at 2s and 4s. Kept short on
// purpose: the deletions themselves are already done, the admin is waiting on a verdict, and a
// manual retry button covers the cases a short backoff cannot.
export const SYNC_RETRY_DELAY_MS = 2000;

const REMOVE_EMOTE_MUTATION = `
  mutation RemoveEmote($setId: ObjectID!, $emoteId: ObjectID!) {
    emoteSet(id: $setId) {
      emotes(id: $emoteId, action: REMOVE) {
        id
      }
    }
  }
`;

/** The one thing that makes this run a *delete* — everything else lives in the engine. */
const REMOVE_OPERATION: RunOperation = {
  label: 'mass delete',
  buildRequest: (setId, emote) => ({
    query: REMOVE_EMOTE_MUTATION,
    variables: { setId, emoteId: emote.sevenTvEmoteId },
  }),
};

/** The public input contract for a delete/restore run — deliberately its own interface, not an
 *  alias of the engine's `RunQueueEmote`: since #70 that engine type also serves import runs and
 *  carries an optional `emoteId` and a queue `key`, neither of which a caller here should have to
 *  think about. `startDelete`/`startRestore` mint the `key` themselves (mirrored from `emoteId` —
 *  see R3 in docs/DECISIONS.md), so every existing call site keeps building this exact shape. */
export interface DeleteQueueEmote {
  emoteId: string;
  sevenTvEmoteId: string;
  name: string;
}
/** Historical aliases — the panel and both host pages import these names. */
export type DeleteItemStatus = RunItemStatus;
export type DeleteQueueItem = RunQueueItem;

/** Outcome of reporting the finished run back to our own API (not to 7TV).
 *  'partial' means the call succeeded but the backend archived fewer emotes than we reported. */
export type SyncReportState = 'idle' | 'pending' | 'succeeded' | 'partial' | 'failed';

@Injectable({ providedIn: 'root' })
export class SevenTvDeleteService {
  private readonly emoteAdminService = inject(EmoteAdminService);

  /** Own engine instance (not a shared singleton), so `isRunning` can never mean "the *other*
   *  service is busy". All pacing/backoff/token mechanics live there — see SevenTvRunEngine. */
  private readonly engine = new SevenTvRunEngine(
    inject(HttpClient),
    inject(SevenTvTokenService),
    inject(TranslocoService),
  );

  private currentChannelName: string | null = null;
  private lastReportedIds: string[] = [];

  readonly queue = this.engine.queue;
  readonly isRunning = this.engine.isRunning;
  readonly rateLimitPauseSeconds = this.engine.rateLimitPauseSeconds;
  readonly progress = this.engine.progress;

  /** State of the closing sync-deleted call. Consumers must wait for a terminal value before
   *  optimistically removing rows: 'failed'/'partial' means the backend does not (fully) know about
   *  the deletion yet, so filtering the list client-side would show a state that isn't real. */
  readonly syncReport = signal<SyncReportState>('idle');

  /** The finished run, kept for the summary/protocol UI (A6). Cleared on reset() — once the panel
   *  is dismissed, the downloaded protocol file is the only remaining artifact, by design. */
  readonly lastRun = signal<{ setId: string; channelName: string; result: RunResult } | null>(null);

  startDelete(setId: string, channelName: string, emotes: DeleteQueueEmote[]): void {
    // key mirrors emoteId — the two services and the panels only ever build fully-populated rows,
    // so the queue key and the internal id are the same value here (see R3 in docs/DECISIONS.md).
    const queueEmotes: RunQueueEmote[] = emotes.map((emote) => ({ ...emote, key: emote.emoteId }));
    const started = this.engine.start(setId, queueEmotes, REMOVE_OPERATION, (result) =>
      this.onRunComplete(setId, channelName, result),
    );
    if (!started) {
      return;
    }
    this.currentChannelName = channelName;
    this.syncReport.set('idle');
    this.lastRun.set(null);
  }

  cancel(): void {
    this.engine.cancel();
  }

  /** Clears the panel after the admin has acknowledged a finished/cancelled run. Also drops the
   *  run's channel and reported ids: with the panel gone there is nothing left to retry against. */
  reset(): void {
    this.engine.reset();
    this.syncReport.set('idle');
    this.currentChannelName = null;
    this.lastReportedIds = [];
    this.lastRun.set(null);
  }

  /** The panel is a root-service singleton, so a finished run used to follow the user into the
   *  next channel's workspace, still showing the previous channel's counts. A *running* run is
   *  deliberately left alone — hiding it would be worse than showing it on the wrong page, and it
   *  still needs its channel for the closing sync call. */
  resetIfChannelChanged(channelName: string): void {
    if (
      this.isRunning() ||
      this.currentChannelName === null ||
      this.currentChannelName === channelName
    ) {
      return;
    }
    this.reset();
  }

  /** Manual retry for the closing report. The 7TV deletions are long done at this point, so this
   *  only re-sends the bookkeeping call — safe to repeat, ids already archived come back in
   *  notFoundIds. */
  retrySyncReport(): void {
    if (
      this.syncReport() === 'pending' ||
      this.lastReportedIds.length === 0 ||
      this.currentChannelName === null
    ) {
      return;
    }

    this.reportDeleted(this.currentChannelName, this.lastReportedIds);
  }

  private onRunComplete(setId: string, channelName: string, result: RunResult): void {
    this.lastRun.set({ setId, channelName, result });
    if (result.doneIds.length > 0) {
      this.lastReportedIds = result.doneIds;
      this.reportDeleted(channelName, result.doneIds);
    }
  }

  private reportDeleted(channelName: string, emoteIds: string[]): void {
    this.syncReport.set('pending');

    this.emoteAdminService
      .syncDeleted(channelName, emoteIds)
      .pipe(
        // A 429 is the realistic case: sync-deleted shares a rate-limit budget with other calls, and
        // a swallowed 429 used to look exactly like success. A 401 (session expired during a long
        // run) cannot be fixed by waiting, so it is not retried.
        retry({
          count: MAX_AUTOMATIC_SYNC_RETRIES,
          delay: (error: HttpErrorResponse, attempt) =>
            error.status === 401 || error.status === 403
              ? throwError(() => error)
              : timer(SYNC_RETRY_DELAY_MS * attempt),
        }),
      )
      .subscribe({
        next: (result: SyncDeletedResult) =>
          // notFoundIds covers ids the backend could not archive (unknown, foreign channel, already
          // archived). All of them coming back is indistinguishable from success in the raw numbers,
          // which is why the result is evaluated at all instead of being discarded.
          this.syncReport.set(result.archivedCount >= emoteIds.length ? 'succeeded' : 'partial'),
        error: () => this.syncReport.set('failed'),
      });
  }
}
