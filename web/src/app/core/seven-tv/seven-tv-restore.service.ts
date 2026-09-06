import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { inject, Injectable, signal } from '@angular/core';
import { TranslocoService } from '@jsverse/transloco';
import { retry, throwError, timer } from 'rxjs';

import { ChannelService } from '../channels/channel.service';
import { EmoteAdminService, SyncRestoredResult } from '../emotes/emote-admin.service';
import {
  DeleteQueueEmote,
  MAX_AUTOMATIC_SYNC_RETRIES,
  SYNC_RETRY_DELAY_MS,
  SyncReportState,
} from './seven-tv-delete.service';
import { RunOperation, RunQueueEmote, RunResult, SevenTvRunEngine } from './seven-tv-run-engine';
import { SevenTvTokenService } from './seven-tv-token.service';

/** Same shape as the delete's REMOVE, with ADD and the alias to restore under. `name` restores the
 *  chat alias the emote had at delete time — without it 7TV falls back to the emote's default name,
 *  which for renamed emotes would not be the one the chat knows. */
const ADD_EMOTE_MUTATION = `
  mutation AddEmote($setId: ObjectID!, $emoteId: ObjectID!, $name: String) {
    emoteSet(id: $setId) {
      emotes(id: $emoteId, action: ADD, name: $name) {
        id
      }
    }
  }
`;

const ADD_OPERATION: RunOperation = {
  label: 'restore',
  buildRequest: (setId, emote) => ({
    query: ADD_EMOTE_MUTATION,
    variables: { setId, emoteId: emote.sevenTvEmoteId, name: emote.name },
  }),
};

/** Outcome of the closing resync trigger. 'cooldown' is not a failure: the per-channel cooldown
 *  (429) means a sync just ran or is about to — the periodic worker heals the view within its
 *  60s tick either way. */
export type ResyncTriggerState = 'idle' | 'pending' | 'succeeded' | 'cooldown' | 'failed';

/**
 * One restore run, from the moment it starts to the moment both closing calls are done. Everything
 * the two asynchronous follow-ups need hangs off *this* object, never off a field next to the
 * service (R15, #72, T12) — see the identical note on `DeleteRunInfo` in
 * `seven-tv-delete.service.ts` and on `ImportRunInfo` in `seven-tv-import.service.ts`. A restore has
 * *two* callbacks racing a superseded run (`sync-restored` and `resync`), each checked
 * independently: one settling first must not stop the other's guard from applying.
 */
interface RestoreRunInfo {
  channelName: string;
  /** `null` while the run is in flight; set once the engine reports the run complete. */
  result: RunResult | null;
}

/**
 * The restore half of A6: re-adds emotes to the 7TV set, in the browser, over the same run engine
 * (pacing, backoff, token) as the delete — ADD draws tickets from the same `emote_set_change`
 * bucket. Zero-knowledge holds: the write token never leaves the browser.
 *
 * A finished run reports itself twice, for two different reasons. `sync-restored` is the
 * bookkeeping call (mirror of the delete's `sync-deleted`): it un-archives the rows and — the
 * reason it exists at all — writes the `emotes.syncRestored` audit entry; before it, a restore
 * only ever appeared in the log as an anonymous `channel.resync`, or under the resync cooldown
 * not at all (user decision 2026-08-02, revising the original "no sync-restored endpoint" call).
 * The A8 resync trigger stays on top as reconciliation against 7TV as the authority — aliases the
 * run could not restore, and anything else that drifted.
 */
@Injectable({ providedIn: 'root' })
export class SevenTvRestoreService {
  private readonly channelService = inject(ChannelService);
  private readonly emoteAdminService = inject(EmoteAdminService);

  /** Own engine instance — see the identical note in SevenTvDeleteService. */
  private readonly engine = new SevenTvRunEngine(
    inject(HttpClient),
    inject(SevenTvTokenService),
    inject(TranslocoService),
  );

  /** The run every asynchronous follow-up is bound to (R15). */
  private run: RestoreRunInfo | null = null;

  readonly queue = this.engine.queue;
  readonly isRunning = this.engine.isRunning;
  readonly rateLimitPauseSeconds = this.engine.rateLimitPauseSeconds;
  readonly progress = this.engine.progress;

  /** State of the closing sync-restored call — same contract as the delete's syncReport. */
  readonly syncReport = signal<SyncReportState>('idle');

  readonly resyncTrigger = signal<ResyncTriggerState>('idle');

  startRestore(setId: string, channelName: string, emotes: DeleteQueueEmote[]): void {
    // Same key-mirrors-emoteId reasoning as the delete service (see R3 in docs/DECISIONS.md).
    const queueEmotes: RunQueueEmote[] = emotes.map((emote) => ({ ...emote, key: emote.emoteId }));
    const started: RestoreRunInfo = { channelName, result: null };
    const engineStarted = this.engine.start(setId, queueEmotes, ADD_OPERATION, (result) =>
      this.onRunComplete(started, result),
    );
    if (!engineStarted) {
      // Refused (already running, empty list, no token) — leave every signal as it was.
      return;
    }
    this.run = started;
    this.syncReport.set('idle');
    this.resyncTrigger.set('idle');
  }

  cancel(): void {
    this.engine.cancel();
  }

  reset(): void {
    this.engine.reset();
    this.syncReport.set('idle');
    this.resyncTrigger.set('idle');
    this.run = null;
  }

  /** Same page-follows-user reasoning as the delete service's counterpart. */
  resetIfChannelChanged(channelName: string): void {
    if (this.isRunning() || this.run === null || this.run.channelName === channelName) {
      return;
    }
    this.reset();
  }

  /** Manual retry for the closing report — the 7TV re-adds are long done, so this only re-sends
   *  the bookkeeping call. Safe to repeat: ids already un-archived still count as restored. Channel
   *  *and* ids come from the same record, so a retry can never mix one run's ids with another's
   *  channel (R15). */
  retrySyncReport(): void {
    const current = this.run;
    if (
      this.syncReport() === 'pending' ||
      current === null ||
      current.result === null ||
      current.result.doneIds.length === 0
    ) {
      return;
    }

    this.reportRestored(current, current.channelName, current.result.doneIds);
  }

  private onRunComplete(started: RestoreRunInfo, result: RunResult): void {
    if (this.run !== started) {
      // Only reachable via reset()/resetIfChannelChanged() during the run: the shown run is not
      // this one any more, so neither its result nor its bookkeeping belong on screen.
      return;
    }

    const finished: RestoreRunInfo = { ...started, result };
    this.run = finished;

    if (result.doneIds.length === 0) {
      return;
    }
    // Deliberately both, in parallel: the report is bookkeeping + audit trail for exactly these
    // ids, the resync is reconciliation against 7TV as the authority. Neither replaces the other.
    this.reportRestored(finished, finished.channelName, result.doneIds);
    this.resyncTrigger.set('pending');
    this.channelService.resync(finished.channelName).subscribe({
      next: () => this.applyIfCurrent(finished, () => this.resyncTrigger.set('succeeded')),
      error: (error: HttpErrorResponse) =>
        // 429 = the per-channel cooldown: a sync just ran or will run — "coming on its own",
        // reported as such rather than as an error.
        this.applyIfCurrent(finished, () =>
          this.resyncTrigger.set(error.status === 429 ? 'cooldown' : 'failed'),
        ),
    });
  }

  private reportRestored(run: RestoreRunInfo, channelName: string, emoteIds: string[]): void {
    this.syncReport.set('pending');

    this.emoteAdminService
      .syncRestored(channelName, emoteIds)
      .pipe(
        // Same policy as the delete's report: waiting can fix a 429/5xx, not a 401/403.
        retry({
          count: MAX_AUTOMATIC_SYNC_RETRIES,
          delay: (error: HttpErrorResponse, attempt) =>
            error.status === 401 || error.status === 403
              ? throwError(() => error)
              : timer(SYNC_RETRY_DELAY_MS * attempt),
        }),
      )
      .subscribe({
        next: (result: SyncRestoredResult) =>
          this.applyIfCurrent(run, () =>
            this.syncReport.set(result.restoredCount >= emoteIds.length ? 'succeeded' : 'partial'),
          ),
        error: () => this.applyIfCurrent(run, () => this.syncReport.set('failed')),
      });
  }

  /** The R15 guard in one place: an answer that belongs to a superseded run is dropped silently —
   *  no error state, nothing written. The run it belongs to is not on screen any more, and the one
   *  that is must not inherit its outcome. Shared by both closing calls (`sync-restored`, `resync`)
   *  — each checks independently, so one settling does not gate the other. */
  private applyIfCurrent(run: RestoreRunInfo, apply: () => void): void {
    if (this.run !== run) {
      return;
    }
    apply();
  }
}
