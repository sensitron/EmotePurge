import { DOCUMENT } from '@angular/common';
import { Injectable, inject, signal } from '@angular/core';
import { Observable, share } from 'rxjs';

import { EVENT_SOURCE_FACTORY } from './event-source.factory';
import { LIVE_EVENT_TYPES, LiveEvent } from './live-event.model';

export type LiveStatus = 'idle' | 'connecting' | 'open' | 'closed';

// EventSource.CONNECTING / .CLOSED are deliberately NOT referenced: jsdom ships no EventSource
// global at all, so touching the constructor would throw a ReferenceError in every Vitest run.
// The numbers are fixed by the WHATWG spec.
const READY_STATE_CONNECTING = 0;
const READY_STATE_CLOSED = 2;

// Issue #128: a 429 (per-login quota full) frees up again within seconds on the server side, and a
// 503 (Redis away) self-heals on its own — the `_redisSubscribed` latch that keeps a warm process
// answering 200 during an outage is explicitly untouched (DECISIONS 2026-09-01/2026-09-10), so a
// 503 a client actually sees is itself transient. Both are worth retrying; a 401 (revoked session)
// or 404 is not — but `onerror` cannot tell them apart (no status code, no body), so retrying on
// every fatal close is what LiveQuotaService's probe exists to make sense of after the fact.
const INITIAL_RECONNECT_DELAY_MS = 10_000;
const MAX_RECONNECT_DELAY_MS = 60_000;
const RECONNECT_BACKOFF_MULTIPLIER = 2;

/**
 * Subscribes to a server-sent-events endpoint and hands out parsed {@link LiveEvent}s.
 *
 * A transient drop (Api restart, proxy hiccup, and above all the server's 10-minute lifetime cap)
 * leaves the EventSource in CONNECTING and the browser retries on its own — re-running the whole
 * auth pipeline on the way, which is exactly what makes the lifetime cap a session-revocation
 * window. A *fatal* close (401/404/429/503, readyState CLOSED) is different: the browser will not
 * rebuild it, so this service does, on a capped exponential backoff (10 s, 20 s, … up to 60 s,
 * reset after the next successful `open`) rather than hammering a door that might still be shut.
 *
 * There is no polling fallback on purpose: the failure mode is simply today's behaviour, and the
 * manual refresh button stays on every page that uses this.
 */
@Injectable({ providedIn: 'root' })
export class LiveUpdateService {
  private readonly createEventSource = inject(EVENT_SOURCE_FACTORY);
  private readonly document = inject(DOCUMENT);

  private readonly statusSignal = signal<LiveStatus>('idle');

  private readonly fatalCloseSignal = signal(0);

  /** Informational only — service-wide, so with two concurrent streams the last writer wins.
   *  Nothing branches on it; it exists for diagnostics and a possible future indicator. */
  readonly status = this.statusSignal.asReadonly();

  /**
   * Bumped once per *fatal* close (readyState CLOSED — the server answered non-2xx). A counter and
   * not a boolean, so a second failure after the first has been dealt with is still an event a
   * consumer can react to.
   *
   * Exists because this service cannot say *why* the stream died and must not learn: `onerror`
   * carries neither status code nor body, and the alternative — replacing EventSource with a
   * fetch-based reader to read the status — would move the browser's whole reconnect behaviour into
   * our code for one hint. So the signal says "a stream was refused, go and ask"; LiveQuotaService
   * does the asking (issue #42). Deliberately no HttpClient here: keeping this service's dependencies
   * to the EventSource factory and the document is what makes it testable without an HTTP harness.
   */
  readonly fatalCloseCount = this.fatalCloseSignal.asReadonly();

  /** One entry per URL this session has ever subscribed to. Holds no connection — only the (inert)
   *  multicast Observable, so a session that visited twenty channels carries twenty closures. */
  private readonly sharedStreams = new Map<string, Observable<LiveEvent>>();

  /**
   * Multicast per URL, ref-counted: the first subscriber opens the connection, the last one to
   * unsubscribe closes it, and everyone in between shares the same `EventSource`.
   *
   * Still cold — nothing is opened until someone subscribes — and a URL whose subscribers have all
   * left is rebuilt from scratch on the next one, which is what keeps the `switchMap` in
   * {@link liveEvents} working across navigation.
   *
   * Sharing rather than one connection per subscriber, because the subscribers of one URL sit on top
   * of each other: the channel workspace layout and whichever page is routed into it both listen to
   * `/api/channels/{name}/live`, and the admin monitoring page has up to three listeners on
   * `/api/admin/live`. Each of those used to be its own connection, its own auth handshake, and its
   * own slot in ILiveEventStream's connection limits.
   *
   * One behavioural consequence: the reconnect backoff after a fatal close is per connection, not
   * per component. That is the more honest budget — the connection is what failed.
   */
  stream(url: string): Observable<LiveEvent> {
    const shared = this.sharedStreams.get(url);
    if (shared) {
      return shared;
    }

    const created = this.connect(url).pipe(share({ resetOnRefCountZero: true }));
    this.sharedStreams.set(url, created);
    return created;
  }

  /** The single-subscriber connection the multicast above wraps. */
  private connect(url: string): Observable<LiveEvent> {
    return new Observable<LiveEvent>((subscriber) => {
      let source: EventSource | null = null;
      let reconnectTimer: ReturnType<typeof setTimeout> | null = null;
      let reconnectDelayMs = INITIAL_RECONNECT_DELAY_MS;
      // Set once a scheduled reconnect's delay has elapsed while the tab was hidden — the single
      // hand-off point to the visibility listener below. At most one of {reconnectTimer pending,
      // reconnectDue} is ever true, which is what keeps this a single reconnect path per URL rather
      // than two racing ones.
      let reconnectDue = false;

      const open = (): void => {
        this.statusSignal.set('connecting');
        const opened = this.createEventSource(url);
        source = opened;

        opened.onopen = () => {
          this.statusSignal.set('open');
          // A successful connection is proof the backoff has done its job — the next fatal close,
          // whenever it comes, starts back at the short delay rather than continuing to escalate.
          reconnectDelayMs = INITIAL_RECONNECT_DELAY_MS;
        };

        opened.onmessage = (event: MessageEvent) => {
          const parsed = parseLiveEvent(event.data);
          // A malformed frame must never kill the stream — drop it and keep listening.
          if (parsed && parsed.type !== LIVE_EVENT_TYPES.ping) {
            subscriber.next(parsed);
          }
        };

        opened.onerror = () => {
          if (opened.readyState === READY_STATE_CONNECTING) {
            // The browser is already retrying with its own backoff. Doing anything here would
            // either duplicate the connection or cancel a retry that is about to succeed.
            this.statusSignal.set('connecting');
            return;
          }
          if (opened.readyState === READY_STATE_CLOSED) {
            // Fatal (the server answered non-2xx: 401 after a revoked session, 404, 429 over the
            // per-login quota, 503 while Redis is away). No rebuild here — neither erroring nor
            // completing the subscriber, since a reconnect is scheduled below instead — but see the
            // class doc for why retrying on all of these is the right call regardless of cause.
            this.statusSignal.set('closed');
            this.fatalCloseSignal.update((count) => count + 1);
            scheduleReconnect();
          }
        };
      };

      const scheduleReconnect = (): void => {
        const delay = reconnectDelayMs;
        reconnectDelayMs = Math.min(delay * RECONNECT_BACKOFF_MULTIPLIER, MAX_RECONNECT_DELAY_MS);
        reconnectTimer = setTimeout(() => {
          reconnectTimer = null;
          if (this.document.visibilityState === 'visible') {
            open();
            return;
          }
          // Backgrounded tabs get no reconnect attempts of their own — spending a slot to keep a
          // stream nobody is looking at alive would be exactly the budget pressure issue #128 is
          // about. Hand off to the visibility listener instead; it fires the moment the tab is
          // looked at again, which is also the realistic case for "the outage has since resolved".
          reconnectDue = true;
        }, delay);
      };

      // The other half of the hand-off above: a backoff delay that elapsed while hidden is honoured
      // as soon as the tab becomes visible, without waiting out a further delay.
      const onVisibilityChange = (): void => {
        if (!reconnectDue || this.document.visibilityState !== 'visible') {
          return;
        }
        reconnectDue = false;
        source?.close();
        open();
      };

      this.document.addEventListener('visibilitychange', onVisibilityChange);
      open();

      return () => {
        this.document.removeEventListener('visibilitychange', onVisibilityChange);
        if (reconnectTimer !== null) {
          clearTimeout(reconnectTimer);
          reconnectTimer = null;
        }
        reconnectDue = false;
        source?.close();
        source = null;
        this.statusSignal.set('idle');
      };
    });
  }
}

/** Returns null for anything that is not a JSON object carrying a string `type`. */
function parseLiveEvent(data: unknown): LiveEvent | null {
  if (typeof data !== 'string') {
    return null;
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(data);
  } catch {
    return null;
  }
  if (!parsed || typeof parsed !== 'object') {
    return null;
  }
  const candidate = parsed as Partial<LiveEvent>;
  // Unknown types pass through untouched — filtering is the consumer's job, and silently ignoring
  // types this build does not know about is what keeps deployments order-independent.
  return typeof candidate.type === 'string' ? (candidate as LiveEvent) : null;
}
