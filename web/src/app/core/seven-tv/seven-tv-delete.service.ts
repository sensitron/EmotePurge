import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { computed, inject, Injectable, signal } from '@angular/core';
import { TranslocoService } from '@jsverse/transloco';
import {
  Observable,
  Subscription,
  catchError,
  concatMap,
  defer,
  delayWhen,
  from,
  interval,
  map,
  of,
  retry,
  tap,
  throwError,
  timer,
} from 'rxjs';

import { EmoteAdminService, SyncDeletedResult } from '../emotes/emote-admin.service';
import { SevenTvTokenService } from './seven-tv-token.service';

const SEVEN_TV_GQL_ENDPOINT = 'https://7tv.io/v3/gql';
// Starting pace only — the run re-paces itself from 7TV's own numbers the first time it is rate
// limited (see onRateLimited). Deliberately kept aggressive: 7TV's actual quota for the
// `emote_set_change` bucket lives in their database, not in their open-source tree, so the only way
// to learn it is to reach it once. The backoff below makes that safe.
const DELETE_DELAY_MS = 275;
const MAX_AUTOMATIC_SYNC_RETRIES = 2;

/** 7TV's rate-limit bucket guarding every emote-set mutation, one ticket per call.
 *  Source: SevenTV/SevenTV, apps/api/src/http/v3/gql/mutations/emote_sets/mod.rs. */
const RATE_LIMIT_RESOURCE = 'emote_set_change';
const RATE_LIMIT_ERROR_CODE = 'RATE_LIMIT_EXCEEDED';
/** Added on top of the server-reported reset so a clock skew of a few hundred ms cannot make the
 *  first request after the pause land inside the still-closed window. */
const RATE_LIMIT_BUFFER_MS = 500;
/** Used when 7TV rejects without telling us when to come back (HTTP 429 from the *global* bucket —
 *  that one is enforced at the HTTP layer, and its headers are not CORS-exposed). Their windows are
 *  60s, so a full minute is the safe assumption. */
const RATE_LIMIT_FALLBACK_WAIT_MS = 60_000;
const MAX_RATE_LIMIT_RETRIES = 5;
/** Aim slightly *below* the measured quota. 7TV uses a fixed window, so pacing exactly at the limit
 *  puts every window's last request on the boundary. */
const RATE_LIMIT_PACING_MARGIN = 1.1;
// Multiplied by the attempt number, so the two automatic attempts land at 2s and 4s. Kept short on
// purpose: the deletions themselves are already done, the admin is waiting on a verdict, and a
// manual retry button covers the cases a short backoff cannot.
const SYNC_RETRY_DELAY_MS = 2000;

const REMOVE_EMOTE_MUTATION = `
  mutation RemoveEmote($setId: ObjectID!, $emoteId: ObjectID!) {
    emoteSet(id: $setId) {
      emotes(id: $emoteId, action: REMOVE) {
        id
      }
    }
  }
`;

export interface DeleteQueueEmote {
  emoteId: string; // internal id — used for sync-deleted and for the host page's optimistic removal.
  sevenTvEmoteId: string;
  name: string;
}

export type DeleteItemStatus = 'pending' | 'in-progress' | 'done' | 'failed' | 'cancelled';

export interface DeleteQueueItem extends DeleteQueueEmote {
  status: DeleteItemStatus;
  errorMessage?: string;
}

type DeleteOneResult = { success: true } | { success: false; errorMessage: string };

/** The rate-limit numbers 7TV mirrors into a rejected mutation's `extensions.headers`. All values
 *  arrive as strings; `reset` is in seconds. Any of them can be missing. */
interface RateLimitInfo {
  limit: number | null;
  remaining: number | null;
  reset: number | null;
  used: number | null;
}

/** Thrown internally so RxJS `retry` can drive the wait. Not an `Error` subclass on purpose — it is
 *  a control-flow signal, never surfaced to the user. */
class RateLimitHit {
  constructor(readonly info: RateLimitInfo) {}
}

interface SevenTvGqlError {
  message?: string;
  extensions?: {
    code?: string;
    status?: number;
    headers?: Record<string, string>;
  };
}

function parseHeaderNumber(headers: Record<string, string> | undefined, suffix: string) {
  const raw = headers?.[`x-ratelimit-${RATE_LIMIT_RESOURCE}-${suffix}`];
  if (raw === undefined) {
    return null;
  }
  const parsed = Number.parseInt(raw, 10);
  return Number.isFinite(parsed) ? parsed : null;
}

function isRateLimitError(error: SevenTvGqlError): boolean {
  return error.extensions?.code === RATE_LIMIT_ERROR_CODE || error.extensions?.status === 429;
}

function readRateLimitInfo(error: SevenTvGqlError): RateLimitInfo {
  const headers = error.extensions?.headers;
  return {
    limit: parseHeaderNumber(headers, 'limit'),
    remaining: parseHeaderNumber(headers, 'remaining'),
    reset: parseHeaderNumber(headers, 'reset'),
    used: parseHeaderNumber(headers, 'used'),
  };
}

/** Outcome of reporting the finished run back to our own API (not to 7TV).
 *  'partial' means the call succeeded but the backend archived fewer emotes than we reported. */
export type SyncReportState = 'idle' | 'pending' | 'succeeded' | 'partial' | 'failed';

@Injectable({ providedIn: 'root' })
export class SevenTvDeleteService {
  private readonly http = inject(HttpClient);
  private readonly tokenService = inject(SevenTvTokenService);
  private readonly emoteAdminService = inject(EmoteAdminService);
  private readonly translocoService = inject(TranslocoService);

  private runSubscription: Subscription | null = null;
  private countdownSubscription: Subscription | null = null;
  private currentChannelName: string | null = null;
  private lastReportedIds: string[] = [];

  /** Pacing state for the running job. `currentDelayMs` is the only one the queue reads; the rest
   *  exist to derive it from 7TV's answer on the first rejection. */
  private currentDelayMs = DELETE_DELAY_MS;
  private windowStartedAt = 0;
  private requestsInWindow = 0;
  private roundTripTotalMs = 0;
  private roundTripCount = 0;
  private pacingAdapted = false;
  private runStartedAt = 0;
  private rateLimitHits = 0;
  /** Timestamp of every attempt, kept for the closing measurement. 7TV's limiter is a fixed 60s
   *  window, so the number that matters is the peak within any 60s span — not the run's average. */
  private requestTimestamps: number[] = [];

  readonly queue = signal<DeleteQueueItem[]>([]);
  readonly isRunning = signal(false);

  /** Seconds left on a rate-limit pause, or null when not waiting. Without this the progress bar
   *  simply stops moving for up to a minute, which is indistinguishable from a hang. */
  readonly rateLimitPauseSeconds = signal<number | null>(null);
  readonly progress = computed(() => {
    const items = this.queue();
    const finished = items.filter(
      (item) => item.status === 'done' || item.status === 'failed',
    ).length;
    return { finished, total: items.length };
  });

  /** State of the closing sync-deleted call. Consumers must wait for a terminal value before
   *  optimistically removing rows: 'failed'/'partial' means the backend does not (fully) know about
   *  the deletion yet, so filtering the list client-side would show a state that isn't real. */
  readonly syncReport = signal<SyncReportState>('idle');

  startDelete(setId: string, channelName: string, emotes: DeleteQueueEmote[]): void {
    if (this.isRunning() || emotes.length === 0) {
      return;
    }

    const token = this.tokenService.getToken();
    if (!token) {
      return;
    }

    this.currentChannelName = channelName;
    this.queue.set(emotes.map((emote) => ({ ...emote, status: 'pending' as DeleteItemStatus })));
    this.syncReport.set('idle');
    this.isRunning.set(true);
    this.resetPacing();

    this.runSubscription = from(emotes)
      .pipe(
        concatMap((emote) => {
          this.setStatus(emote.emoteId, 'in-progress');
          return this.deleteWithBackoff(setId, emote.sevenTvEmoteId, token).pipe(
            tap((result) =>
              this.setStatus(
                emote.emoteId,
                result.success ? 'done' : 'failed',
                result.success ? undefined : result.errorMessage,
              ),
            ),
            // delayWhen, not delay: the pace is re-derived mid-run once 7TV tells us its real quota,
            // and a plain delay() would have captured the starting value forever.
            delayWhen(() => timer(this.currentDelayMs)),
          );
        }),
      )
      .subscribe({
        complete: () => this.finish(),
        error: () => this.finish(),
      });
  }

  /** Cancel = unsubscribing the RxJS chain — idiomatic and simpler than hand-rolled cooperative
   *  cancellation. Items already 'done'/'failed' keep their outcome; the rest become 'cancelled'. */
  cancel(): void {
    this.runSubscription?.unsubscribe();
    this.runSubscription = null;
    // Unsubscribing already kills a pending backoff timer; this only clears its countdown display.
    this.endPause();
    this.queue.update((items) =>
      items.map((item) =>
        item.status === 'pending' || item.status === 'in-progress'
          ? { ...item, status: 'cancelled' }
          : item,
      ),
    );
    this.finish();
  }

  /** Clears the panel after the admin has acknowledged a finished/cancelled run. Also drops the
   *  run's channel and reported ids: with the panel gone there is nothing left to retry against. */
  reset(): void {
    this.queue.set([]);
    this.syncReport.set('idle');
    this.currentChannelName = null;
    this.lastReportedIds = [];
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

  /** One emote, including waiting out any rate limit 7TV imposes. A rate-limited attempt is *not* a
   *  failure: the emote is retried after the server-stated reset, so a large run finishes instead of
   *  burning through its queue against a closed window. */
  private deleteWithBackoff(
    setId: string,
    sevenTvEmoteId: string,
    token: string,
  ): Observable<DeleteOneResult> {
    return this.deleteOne(setId, sevenTvEmoteId, token).pipe(
      retry({
        count: MAX_RATE_LIMIT_RETRIES,
        delay: (error) => {
          if (!(error instanceof RateLimitHit)) {
            return throwError(() => error);
          }
          const waitMs = this.onRateLimited(error.info);
          return timer(waitMs).pipe(tap(() => this.endPause()));
        },
      }),
      catchError(() =>
        // Only a RateLimitHit can get here — deleteOne turns everything else into a result value.
        of({
          success: false as const,
          errorMessage: this.translocoService.translate('massDelete.errors.rateLimitedGaveUp'),
        }),
      ),
    );
  }

  private deleteOne(
    setId: string,
    sevenTvEmoteId: string,
    token: string,
  ): Observable<DeleteOneResult> {
    // defer, so every retry re-runs the request *and* re-stamps its own timing.
    return defer(() => {
      const startedAt = Date.now();
      this.requestsInWindow += 1;
      this.requestTimestamps.push(startedAt);

      return this.http
        .post<{ errors?: SevenTvGqlError[] }>(
          SEVEN_TV_GQL_ENDPOINT,
          { query: REMOVE_EMOTE_MUTATION, variables: { setId, emoteId: sevenTvEmoteId } },
          { headers: { Authorization: `Bearer ${token}` } },
        )
        .pipe(
          tap(() => this.recordRoundTrip(startedAt)),
          map((response): DeleteOneResult => {
            const gqlError = response?.errors?.[0];
            if (!gqlError) {
              return { success: true };
            }
            // 7TV answers a rate-limited mutation with HTTP 200 and the rejection inside `errors`
            // (async-graphql never touches the status code), so this is the only place it surfaces.
            if (isRateLimitError(gqlError)) {
              throw new RateLimitHit(readRateLimitInfo(gqlError));
            }
            return {
              success: false,
              errorMessage: gqlError.message ?? '',
            };
          }),
          catchError((error) => {
            if (error instanceof RateLimitHit) {
              return throwError(() => error);
            }
            const httpError = error as HttpErrorResponse;
            // The *global* bucket is enforced at the HTTP layer and does yield a real 429. Its
            // headers are not CORS-exposed, so we back off blind rather than give up.
            if (httpError.status === 429) {
              return throwError(
                () => new RateLimitHit({ limit: null, remaining: null, reset: null, used: null }),
              );
            }
            return of<DeleteOneResult>({
              success: false,
              errorMessage: this.describeHttpError(httpError),
            });
          }),
        );
    });
  }

  /** Called on every rejection. Logs what 7TV reported (this is how we learn the real quota — the
   *  numbers exist nowhere else, see docs/DECISIONS.md), re-paces the run once, and returns how long
   *  to wait. */
  private onRateLimited(info: RateLimitInfo): number {
    const waitMs =
      info.reset !== null ? info.reset * 1000 + RATE_LIMIT_BUFFER_MS : RATE_LIMIT_FALLBACK_WAIT_MS;
    const attemptsThisWindow = this.requestsInWindow;
    this.rateLimitHits += 1;

    // Re-pace only from a rejection that actually carries numbers. A blind HTTP 429 (global bucket,
    // headers not CORS-exposed) would otherwise "teach" us a quota of one request per minute.
    const informative = info.reset !== null && (info.limit !== null || attemptsThisWindow > 1);
    if (!this.pacingAdapted && informative) {
      this.adaptPacing(info, attemptsThisWindow);
    }

    console.info('[EmotePurge] 7TV rate limit reached', {
      resource: RATE_LIMIT_RESOURCE,
      reportedLimit: info.limit,
      reportedUsed: info.used,
      resetSeconds: info.reset,
      requestsSentThisWindow: attemptsThisWindow,
      msSinceWindowStart: Date.now() - this.windowStartedAt,
      averageRoundTripMs: Math.round(this.averageRoundTripMs()),
      newDelayMs: this.currentDelayMs,
      waitMs,
    });

    this.startPause(waitMs);
    return waitMs;
  }

  /** Derives a sustainable delay from the one data point 7TV gives us. `limit` comes from the
   *  header when present; otherwise the number of requests we got through this window *is* the
   *  quota. The window length is likewise derived: elapsed-so-far plus the remaining reset. */
  private adaptPacing(info: RateLimitInfo, attemptsThisWindow: number): void {
    const limit = info.limit ?? Math.max(1, attemptsThisWindow - 1);
    const elapsedMs = Date.now() - this.windowStartedAt;
    const windowMs =
      info.reset !== null ? elapsedMs + info.reset * 1000 : RATE_LIMIT_FALLBACK_WAIT_MS;

    // Subtract the observed round-trip: our delay sits *between* requests, so the achieved rate is
    // driven by delay + latency, not by the delay alone.
    const targetCycleMs = (windowMs / limit) * RATE_LIMIT_PACING_MARGIN;
    this.currentDelayMs = Math.max(0, Math.round(targetCycleMs - this.averageRoundTripMs()));
    this.pacingAdapted = true;
  }

  private recordRoundTrip(startedAt: number): void {
    this.roundTripTotalMs += Date.now() - startedAt;
    this.roundTripCount += 1;
  }

  private averageRoundTripMs(): number {
    return this.roundTripCount === 0 ? 0 : this.roundTripTotalMs / this.roundTripCount;
  }

  private resetPacing(): void {
    this.currentDelayMs = DELETE_DELAY_MS;
    this.windowStartedAt = Date.now();
    this.runStartedAt = this.windowStartedAt;
    this.requestsInWindow = 0;
    this.roundTripTotalMs = 0;
    this.roundTripCount = 0;
    this.pacingAdapted = false;
    this.rateLimitHits = 0;
    this.requestTimestamps = [];
    this.endPause();
  }

  /** How many requests we packed into the busiest 60-second span of the run — the figure that is
   *  directly comparable to a `requests / interval_seconds` quota. A run that ends without a single
   *  rate-limit hit proves 7TV tolerates at least this rate for the acting user's role, which is the
   *  only empirical handle we have on a quota that is not published anywhere. */
  private peakRequestsPer60s(): number {
    let peak = 0;
    let windowStart = 0;
    for (let i = 0; i < this.requestTimestamps.length; i++) {
      while (this.requestTimestamps[i] - this.requestTimestamps[windowStart] >= 60_000) {
        windowStart++;
      }
      peak = Math.max(peak, i - windowStart + 1);
    }
    return peak;
  }

  private logRunSummary(): void {
    if (this.requestTimestamps.length === 0) {
      return;
    }
    const statuses = this.queue().map((item) => item.status);
    console.info('[EmotePurge] 7TV mass delete finished', {
      requested: statuses.length,
      succeeded: statuses.filter((status) => status === 'done').length,
      failed: statuses.filter((status) => status === 'failed').length,
      cancelled: statuses.filter((status) => status === 'cancelled').length,
      requestsSent: this.requestTimestamps.length,
      durationMs: Date.now() - this.runStartedAt,
      peakRequestsPer60s: this.peakRequestsPer60s(),
      averageRoundTripMs: Math.round(this.averageRoundTripMs()),
      rateLimitHits: this.rateLimitHits,
      delayMs: this.currentDelayMs,
    });
  }

  private startPause(waitMs: number): void {
    this.countdownSubscription?.unsubscribe();
    this.rateLimitPauseSeconds.set(Math.ceil(waitMs / 1000));
    this.countdownSubscription = interval(1000).subscribe(() => {
      const left = this.rateLimitPauseSeconds();
      if (left !== null) {
        this.rateLimitPauseSeconds.set(Math.max(0, left - 1));
      }
    });
  }

  /** Ends the pause and opens a fresh accounting window — 7TV's limiter is a fixed window, so once
   *  the reset has passed the previous window's usage is gone. */
  private endPause(): void {
    this.countdownSubscription?.unsubscribe();
    this.countdownSubscription = null;
    this.rateLimitPauseSeconds.set(null);
    this.windowStartedAt = Date.now();
    this.requestsInWindow = 0;
  }

  private describeHttpError(error: HttpErrorResponse): string {
    if (error.status === 401 || error.status === 403) {
      // Invalid/expired token — drop it so the UI falls back to the token-input prompt instead
      // of silently reusing the same bad token on the next delete attempt.
      this.tokenService.clearToken();
      return this.translocoService.translate('massDelete.errors.tokenInvalid');
    }
    if (error.status === 429) {
      return this.translocoService.translate('massDelete.errors.rateLimited');
    }
    if (error.status === 0) {
      return this.translocoService.translate('massDelete.errors.networkError');
    }
    return this.translocoService.translate('massDelete.errors.genericStatus', {
      status: error.status,
    });
  }

  private setStatus(emoteId: string, status: DeleteItemStatus, errorMessage?: string): void {
    this.queue.update((items) =>
      items.map((item) => (item.emoteId === emoteId ? { ...item, status, errorMessage } : item)),
    );
  }

  private finish(): void {
    this.isRunning.set(false);
    this.runSubscription = null;
    this.endPause();
    this.logRunSummary();

    const channelName = this.currentChannelName;
    const doneIds = this.queue()
      .filter((item) => item.status === 'done')
      .map((item) => item.emoteId);

    if (channelName && doneIds.length > 0) {
      this.lastReportedIds = doneIds;
      this.reportDeleted(channelName, doneIds);
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
