import { DOCUMENT } from '@angular/common';
import { Injectable, inject, signal } from '@angular/core';
import { Observable } from 'rxjs';

import { EVENT_SOURCE_FACTORY } from './event-source.factory';
import { LIVE_EVENT_TYPES, LiveEvent } from './live-event.model';

export type LiveStatus = 'idle' | 'connecting' | 'open' | 'closed';

// EventSource.CONNECTING / .CLOSED are deliberately NOT referenced: jsdom ships no EventSource
// global at all, so touching the constructor would throw a ReferenceError in every Vitest run.
// The numbers are fixed by the WHATWG spec.
const READY_STATE_CONNECTING = 0;
const READY_STATE_CLOSED = 2;

/**
 * Subscribes to a server-sent-events endpoint and hands out parsed {@link LiveEvent}s.
 *
 * Deliberately minimal: reconnecting is the browser's job. A transient drop (Api restart, proxy
 * hiccup, and above all the server's 10-minute lifetime cap) leaves the EventSource in CONNECTING
 * and it retries on its own — re-running the whole auth pipeline on the way, which is exactly what
 * makes the lifetime cap a session-revocation window. Only a *fatal* close (401/404/503, readyState
 * CLOSED) ends the stream, and then rebuilding it in a loop would just hammer a door that is shut;
 * the one concession is a single retry when the tab becomes visible again.
 *
 * There is no polling fallback on purpose: the failure mode is simply today's behaviour, and the
 * manual refresh button stays on every page that uses this.
 */
@Injectable({ providedIn: 'root' })
export class LiveUpdateService {
  private readonly createEventSource = inject(EVENT_SOURCE_FACTORY);
  private readonly document = inject(DOCUMENT);

  private readonly statusSignal = signal<LiveStatus>('idle');

  /** Informational only — service-wide, so with two concurrent streams the last writer wins.
   *  Nothing branches on it; it exists for diagnostics and a possible future indicator. */
  readonly status = this.statusSignal.asReadonly();

  /**
   * Cold: the connection is opened per subscriber and closed again on unsubscribe, so a `switchMap`
   * over a changing URL tears the old channel's connection down by itself.
   */
  stream(url: string): Observable<LiveEvent> {
    return new Observable<LiveEvent>((subscriber) => {
      let source: EventSource | null = null;
      let retryUsed = false;

      const open = (): void => {
        this.statusSignal.set('connecting');
        const opened = this.createEventSource(url);
        source = opened;

        opened.onopen = () => this.statusSignal.set('open');

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
            // Fatal (the server answered non-2xx: 401 after a revoked session, 404, 503 over limit).
            // No rebuild: neither erroring nor completing the subscriber, because the visibility
            // retry below still needs it alive.
            this.statusSignal.set('closed');
          }
        };
      };

      // Exactly one extra attempt per subscription: coming back to a tab that was backgrounded for
      // hours is the realistic case where a fatal close (expired session, restarted Api) has since
      // resolved itself. Anything beyond that would be a reconnect loop by another name.
      const onVisibilityChange = (): void => {
        if (retryUsed || this.document.visibilityState !== 'visible') {
          return;
        }
        if (source?.readyState !== READY_STATE_CLOSED) {
          return;
        }
        retryUsed = true;
        source.close();
        open();
      };

      this.document.addEventListener('visibilitychange', onVisibilityChange);
      open();

      return () => {
        this.document.removeEventListener('visibilitychange', onVisibilityChange);
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
