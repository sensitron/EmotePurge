import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Service, inject, signal } from '@angular/core';
import { TranslocoService } from '@jsverse/transloco';
import { retry, throwError, timer } from 'rxjs';

import { ChannelService } from '../channels/channel.service';
import { EmoteAdminService } from '../emotes/emote-admin.service';
import { ImportOrigin, ImportRow } from './import-source';
import {
  MAX_AUTOMATIC_SYNC_RETRIES,
  SYNC_RETRY_DELAY_MS,
  SyncReportState,
} from './seven-tv-delete.service';
import { ResyncTriggerState } from './seven-tv-restore.service';
import { RunOperation, RunQueueEmote, RunResult, SevenTvRunEngine } from './seven-tv-run-engine';
import { SevenTvTokenService } from './seven-tv-token.service';

/** The same ADD the restore run uses — an import *is* an ADD, only with rows that come from
 *  somewhere else. `name` carries the source alias so the copy keeps the name the source channel
 *  knew it by; without it 7TV would fall back to the emote's default name. */
const ADD_EMOTE_MUTATION = `
  mutation AddEmote($setId: ObjectID!, $emoteId: ObjectID!, $name: String) {
    emoteSet(id: $setId) {
      emotes(id: $emoteId, action: ADD, name: $name) {
        id
      }
    }
  }
`;

/** 7TV's wording for "you may not write this set", as far as it is known from their frontend. Kept
 *  as a list of substrings rather than one regex so a newly observed phrasing is one line to add.
 *  Matched against the *raw* GQL text, which is why the comparison is lower-cased. */
const PRIVILEGE_ERROR_FRAGMENTS = ['insufficient privileges', 'missing permission'];

/**
 * Whether a failed row means "this token may not write this set at all". True for it stops the run:
 * every remaining row would fail identically, and 7TV counts each attempt against the rate-limit
 * bucket. Deliberately narrow — a rate-limit give-up (translated text, `null` status) and a network
 * error (status `0`) are row failures, not privilege failures, and the run keeps going for them.
 */
function abortsForMissingPrivileges(failure: {
  message: string;
  httpStatus: number | null;
}): boolean {
  if (failure.httpStatus === 401 || failure.httpStatus === 403) {
    return true;
  }
  const message = failure.message.toLowerCase();
  return PRIVILEGE_ERROR_FRAGMENTS.some((fragment) => message.includes(fragment));
}

/**
 * One import run, from the moment it starts to the moment its bookkeeping is done. Everything the
 * closing calls need hangs off *this* object, never off a field next to the service (R15, #72):
 * the engine sets `isRunning` back to `false` inside `finish()`, i.e. *before* `onComplete` fires
 * the asynchronous follow-up, and the arbiter derives "a run is active" from exactly that signal —
 * so a second import can legitimately start while the first one's `sync-imported`/`resync` are
 * still in flight. With a target channel in one field and the reported keys in another, a late
 * answer (or a manual retry) of run 1 could be applied to run 2's target: emote rows pushed into a
 * third channel and an audit entry naming the wrong origin. Bound to the record, a late answer is
 * simply no longer `run()` and is dropped.
 */
export interface ImportRunInfo {
  targetChannelName: string;
  targetSetId: string;
  origin: ImportOrigin;
  /** `null` while the run is in flight; set once, when the engine reports the run complete. */
  result: RunResult | null;
}

/**
 * The import half of #38/K3: copies emotes into *another* channel's 7TV set, over the same run
 * engine (pacing, backoff, token, queue) as the delete and the restore — ADD draws tickets from the
 * same `emote_set_change` bucket. Zero-knowledge holds: the write token never leaves the browser.
 *
 * Two things separate it from `SevenTvRestoreService`, both deliberate:
 * - **No `resetIfChannelChanged`.** A restore always writes into the channel whose page you are on;
 *   an import writes into a *different* one on purpose, so resetting the run when the page follows
 *   the user would throw away the very run they started (R9).
 * - **The follow-up hangs off `run()`, not off loose fields** — see `ImportRunInfo`.
 *
 * It does not know the `SevenTvRunArbiter`, and does not report to it: the arbiter derives its
 * answer from this service's own `isRunning` signal (its third branch), exactly as it does for
 * delete and restore. Checking whether a run may start is the caller's job.
 */
@Service()
export class SevenTvImportService {
  private readonly channelService = inject(ChannelService);
  private readonly emoteAdminService = inject(EmoteAdminService);

  /** Own engine instance — see the identical note in SevenTvDeleteService. */
  private readonly engine = new SevenTvRunEngine(
    inject(HttpClient),
    inject(SevenTvTokenService),
    inject(TranslocoService),
  );

  /** Not a module-level constant like the delete's and the restore's: `abortOn` writes this
   *  service's own signal, so the operation has to close over the instance. */
  private readonly addOperation: RunOperation = {
    label: 'import',
    buildRequest: (setId, emote) => ({
      query: ADD_EMOTE_MUTATION,
      variables: { setId, emoteId: emote.sevenTvEmoteId, name: emote.name },
    }),
    abortOn: (failure) => {
      const abort = abortsForMissingPrivileges(failure);
      if (abort) {
        this.abortedForPrivileges.set(true);
      }
      return abort;
    },
  };

  readonly queue = this.engine.queue;
  readonly isRunning = this.engine.isRunning;
  readonly rateLimitPauseSeconds = this.engine.rateLimitPauseSeconds;
  readonly progress = this.engine.progress;

  /** The run this service is currently showing — in flight (`result === null`) or finished. The
   *  identity of this object is what every asynchronous follow-up checks itself against. */
  readonly run = signal<ImportRunInfo | null>(null);

  /** State of the closing sync-imported call. Never 'partial': the endpoint answers 204 without a
   *  body, so there is no per-id outcome to compare against. */
  readonly syncReport = signal<SyncReportState>('idle');

  readonly resyncTrigger = signal<ResyncTriggerState>('idle');

  /** True once a row failed for missing 7TV write privileges and the run gave up because of it —
   *  the summary says so instead of listing every cancelled row as an ordinary failure. */
  readonly abortedForPrivileges = signal(false);

  /** `rows` are expected deduplicated (`dedupeImportRows`); the engine never deduplicates. */
  startImport(
    target: { setId: string; channelName: string },
    origin: ImportOrigin,
    rows: ImportRow[],
  ): void {
    // The 7TV id is the only identity an imported row has — the emote does not exist in our
    // database yet, so there is no internal `emoteId` to mirror the key from.
    const queueEmotes: RunQueueEmote[] = rows.map((row) => ({
      key: row.sevenTvEmoteId,
      sevenTvEmoteId: row.sevenTvEmoteId,
      name: row.name,
    }));
    const started: ImportRunInfo = {
      targetChannelName: target.channelName,
      targetSetId: target.setId,
      origin,
      result: null,
    };

    if (
      !this.engine.start(target.setId, queueEmotes, this.addOperation, (result) =>
        this.onRunComplete(started, result),
      )
    ) {
      // Refused (already running, empty list, no token) — leave every signal as it was.
      return;
    }

    this.run.set(started);
    this.syncReport.set('idle');
    this.resyncTrigger.set('idle');
    this.abortedForPrivileges.set(false);
  }

  cancel(): void {
    this.engine.cancel();
  }

  reset(): void {
    this.engine.reset();
    this.run.set(null);
    this.syncReport.set('idle');
    this.resyncTrigger.set('idle');
    this.abortedForPrivileges.set(false);
  }

  /** Manual retry for the closing report — the 7TV adds are long done, so this only re-sends the
   *  bookkeeping call. Safe to repeat: the endpoint only writes an audit entry. Target *and* keys
   *  come from the same record, so a retry can never mix one run's keys with another's channel. */
  retrySyncReport(): void {
    const current = this.run();
    if (
      this.syncReport() === 'pending' ||
      current === null ||
      current.result === null ||
      current.result.doneKeys.length === 0
    ) {
      return;
    }

    this.reportImported(current);
  }

  private onRunComplete(started: ImportRunInfo, result: RunResult): void {
    if (this.run() !== started) {
      // Only reachable via reset() during the run: the shown run is not this one any more, so
      // neither its result nor its bookkeeping belong on screen.
      return;
    }

    // A new object rather than a mutation, so consumers of `run()` actually see the result. From
    // here on this is the record the follow-up is bound to.
    const finished: ImportRunInfo = { ...started, result };
    this.run.set(finished);

    if (result.doneKeys.length === 0) {
      return;
    }

    // Deliberately both, in parallel: the report is the audit trail for exactly these ids, the
    // resync is what actually pulls the new emote rows in from 7TV. Neither replaces the other.
    this.reportImported(finished);
    this.resyncTrigger.set('pending');
    this.channelService.resync(finished.targetChannelName).subscribe({
      next: () => this.applyIfCurrent(finished, () => this.resyncTrigger.set('succeeded')),
      error: (error: HttpErrorResponse) =>
        // 429 = the per-channel cooldown: a sync just ran or will run — "coming on its own",
        // reported as such rather than as an error.
        this.applyIfCurrent(finished, () =>
          this.resyncTrigger.set(error.status === 429 ? 'cooldown' : 'failed'),
        ),
    });
  }

  private reportImported(run: ImportRunInfo): void {
    const doneKeys = run.result?.doneKeys ?? [];
    this.syncReport.set('pending');

    this.emoteAdminService
      .syncImported(run.targetChannelName, {
        sevenTvEmoteIds: doneKeys,
        // A file import sends no source channel even when the file names one: the server rejects
        // `file` *with* a name as `invalid_source_kind` (R3, K2 contract).
        sourceChannelName: run.origin.kind === 'channel' ? run.origin.channelName : null,
        sourceKind: run.origin.kind,
      })
      .pipe(
        // Same policy as the delete's and the restore's report: waiting can fix a 429/5xx, not a
        // 401/403.
        retry({
          count: MAX_AUTOMATIC_SYNC_RETRIES,
          delay: (error: HttpErrorResponse, attempt) =>
            error.status === 401 || error.status === 403
              ? throwError(() => error)
              : timer(SYNC_RETRY_DELAY_MS * attempt),
        }),
      )
      .subscribe({
        next: () => this.applyIfCurrent(run, () => this.syncReport.set('succeeded')),
        error: () => this.applyIfCurrent(run, () => this.syncReport.set('failed')),
      });
  }

  /** The R15 guard in one place: an answer that belongs to a superseded run is dropped silently —
   *  no error state, nothing written. The run it belongs to is not on screen any more, and the one
   *  that is must not inherit its outcome. */
  private applyIfCurrent(run: ImportRunInfo, apply: () => void): void {
    if (this.run() !== run) {
      return;
    }
    apply();
  }
}
