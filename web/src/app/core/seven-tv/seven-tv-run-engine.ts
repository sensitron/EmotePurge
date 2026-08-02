import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Signal, computed, signal } from '@angular/core';
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

import { SevenTvTokenService } from './seven-tv-token.service';

const SEVEN_TV_GQL_ENDPOINT = 'https://7tv.io/v3/gql';
// Starting pace only — the run re-paces itself from 7TV's own numbers the first time it is rate
// limited (see onRateLimited). Deliberately kept aggressive: 7TV's actual quota for the
// `emote_set_change` bucket lives in their database, not in their open-source tree, so the only way
// to learn it is to reach it once. The backoff below makes that safe.
export const RUN_DELAY_MS = 275;

/** 7TV's rate-limit bucket guarding every emote-set mutation, one ticket per call — REMOVE and ADD
 *  alike, which is why one engine paces both the delete and the restore run.
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

export interface RunQueueEmote {
  emoteId: string; // internal id — used for the closing bookkeeping and optimistic list updates.
  sevenTvEmoteId: string;
  name: string;
}

export type RunItemStatus = 'pending' | 'in-progress' | 'done' | 'failed' | 'cancelled';

export interface RunQueueItem extends RunQueueEmote {
  status: RunItemStatus;
  errorMessage?: string;
}

/** What a run does per emote. The engine owns pacing, retries, token handling and the queue; the
 *  operation owns only the mutation — REMOVE for the delete, ADD for the restore. */
export interface RunOperation {
  /** Goes into the closing console measurement: `[EmotePurge] 7TV <label> finished`. */
  readonly label: string;
  buildRequest(
    setId: string,
    emote: RunQueueEmote,
  ): {
    query: string;
    variables: Record<string, unknown>;
  };
}

export interface RunResult {
  doneIds: string[];
  items: RunQueueItem[];
  startedAt: number;
  finishedAt: number;
}

type RunOneResult = { success: true } | { success: false; errorMessage: string };

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

/**
 * The run mechanics behind both 7TV mass mutations (delete since 2026-07-26, restore since A6):
 * sequential queue, adaptive pacing learned from 7TV's rate-limit answers, rate-limit backoff with
 * countdown, token handling. Extracted verbatim from SevenTvDeleteService — the ~350 lines here are
 * identical for REMOVE and ADD because both draw tickets from the same `emote_set_change` bucket.
 *
 * Deliberately a plain class, not a root singleton: the delete and the restore service each hold
 * their *own* instance (`new SevenTvRunEngine(inject(...), ...)`), so `isRunning` stays unambiguous
 * per service. Error message keys stay under `massDelete.errors.*` — shared, not duplicated.
 */
export class SevenTvRunEngine {
  private runSubscription: Subscription | null = null;
  private countdownSubscription: Subscription | null = null;
  private operation: RunOperation | null = null;
  private onComplete: ((result: RunResult) => void) | null = null;

  /** Pacing state for the running job. `currentDelayMs` is the only one the queue reads; the rest
   *  exist to derive it from 7TV's answer on the first rejection. */
  private currentDelayMs = RUN_DELAY_MS;
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

  readonly queue = signal<RunQueueItem[]>([]);
  readonly isRunning = signal(false);

  /** Seconds left on a rate-limit pause, or null when not waiting. Without this the progress bar
   *  simply stops moving for up to a minute, which is indistinguishable from a hang. */
  readonly rateLimitPauseSeconds = signal<number | null>(null);

  readonly progress: Signal<{ finished: number; total: number }> = computed(() => {
    const items = this.queue();
    const finished = items.filter(
      (item) => item.status === 'done' || item.status === 'failed',
    ).length;
    return { finished, total: items.length };
  });

  constructor(
    private readonly http: HttpClient,
    private readonly tokenService: SevenTvTokenService,
    private readonly translocoService: TranslocoService,
  ) {}

  /** Returns false when the run did not start (already running, empty list, no token) — the owning
   *  service must not set up its own per-run state in that case. */
  start(
    setId: string,
    emotes: RunQueueEmote[],
    operation: RunOperation,
    onComplete: (result: RunResult) => void,
  ): boolean {
    if (this.isRunning() || emotes.length === 0) {
      return false;
    }

    const token = this.tokenService.getToken();
    if (!token) {
      return false;
    }

    this.operation = operation;
    this.onComplete = onComplete;
    this.queue.set(emotes.map((emote) => ({ ...emote, status: 'pending' as RunItemStatus })));
    this.isRunning.set(true);
    this.resetPacing();

    this.runSubscription = from(emotes)
      .pipe(
        concatMap((emote) => {
          this.setStatus(emote.emoteId, 'in-progress');
          return this.runWithBackoff(setId, emote, operation, token).pipe(
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
    return true;
  }

  /** Cancel = unsubscribing the RxJS chain — idiomatic and simpler than hand-rolled cooperative
   *  cancellation. Items already 'done'/'failed' keep their outcome; the rest become 'cancelled'. */
  cancel(): void {
    if (!this.isRunning()) {
      return;
    }
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

  /** Clears the queue after the owning service has torn down its own per-run state. */
  reset(): void {
    this.queue.set([]);
  }

  /** One emote, including waiting out any rate limit 7TV imposes. A rate-limited attempt is *not* a
   *  failure: the emote is retried after the server-stated reset, so a large run finishes instead of
   *  burning through its queue against a closed window. */
  private runWithBackoff(
    setId: string,
    emote: RunQueueEmote,
    operation: RunOperation,
    token: string,
  ): Observable<RunOneResult> {
    return this.runOne(setId, emote, operation, token).pipe(
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
        // Only a RateLimitHit can get here — runOne turns everything else into a result value.
        of({
          success: false as const,
          errorMessage: this.translocoService.translate('massDelete.errors.rateLimitedGaveUp'),
        }),
      ),
    );
  }

  private runOne(
    setId: string,
    emote: RunQueueEmote,
    operation: RunOperation,
    token: string,
  ): Observable<RunOneResult> {
    // defer, so every retry re-runs the request *and* re-stamps its own timing.
    return defer(() => {
      const startedAt = Date.now();
      this.requestsInWindow += 1;
      this.requestTimestamps.push(startedAt);

      return this.http
        .post<{ errors?: SevenTvGqlError[] }>(
          SEVEN_TV_GQL_ENDPOINT,
          operation.buildRequest(setId, emote),
          { headers: { Authorization: `Bearer ${token}` } },
        )
        .pipe(
          tap(() => this.recordRoundTrip(startedAt)),
          map((response): RunOneResult => {
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
            return of<RunOneResult>({
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
    this.currentDelayMs = RUN_DELAY_MS;
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
    console.info(`[EmotePurge] 7TV ${this.operation?.label ?? 'run'} finished`, {
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
      // of silently reusing the same bad token on the next attempt.
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

  private setStatus(emoteId: string, status: RunItemStatus, errorMessage?: string): void {
    this.queue.update((items) =>
      items.map((item) => (item.emoteId === emoteId ? { ...item, status, errorMessage } : item)),
    );
  }

  private finish(): void {
    this.isRunning.set(false);
    this.runSubscription = null;
    this.endPause();
    this.logRunSummary();

    const items = this.queue();
    const result: RunResult = {
      doneIds: items.filter((item) => item.status === 'done').map((item) => item.emoteId),
      items,
      startedAt: this.runStartedAt,
      finishedAt: Date.now(),
    };
    this.onComplete?.(result);
  }
}
