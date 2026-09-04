import { HttpClient } from '@angular/common/http';
import { Injectable, computed, effect, inject, signal } from '@angular/core';

import { LiveUpdateService } from './live-update.service';

/** Answer of `GET /api/live/status` — see LiveEndpoints. */
export interface LiveStreamStatus {
  openConnections: number;
  maxPerSubscriber: number;
  perSubscriberLimitReached: boolean;
}

/**
 * Turns "a live stream was refused" into "and here is whether that was your own doing" (issue #42,
 * stage 2).
 *
 * The problem it solves is one of the browser's making: `EventSource.onerror` exposes neither status
 * code nor body, so a refused tab sees only `readyState CLOSED`. A 503 (Redis away) and a 429 (this
 * login already holds six streams) are therefore indistinguishable to the page — which is exactly
 * the confusion that cost half an hour of debugging when the limit was first hit in a live test, and
 * why the page today just goes quiet instead of saying anything.
 *
 * So the reason is fetched rather than inferred: after a fatal close, ask the server how much of the
 * budget this login is holding. Only a *full per-login* budget produces a hint — a refusal while
 * that budget still has room was the process ceiling or the infrastructure, and neither is something
 * the person in front of the screen can act on.
 *
 * Split off from {@link LiveUpdateService} on purpose: that service deliberately depends on nothing
 * but the EventSource factory and the document, and the hint has no business making it need an HTTP
 * harness to test.
 */
@Injectable({ providedIn: 'root' })
export class LiveQuotaService {
  private readonly http = inject(HttpClient);
  private readonly liveUpdate = inject(LiveUpdateService);

  private readonly quotaSignal = signal<LiveStreamStatus | null>(null);

  /**
   * Bumped whenever anything invalidates an answer that is still on its way — today only a stream
   * reopening. A probe whose generation no longer matches is dropped on arrival.
   *
   * Needed because the two are genuinely concurrent: the visibility retry can reopen a stream while
   * the probe explaining the *previous* close is still in flight, and a late "your budget was full"
   * would then land after the clear and stick — `status` stays `open`, so nothing would ever clear
   * it again. The result is the one failure this feature must not have: a warning about live updates
   * on a page whose live updates work.
   */
  private probeGeneration = 0;

  /**
   * The last answer, or null while none applies — either nothing has been refused yet, or a stream
   * has since opened and made the old answer obsolete.
   */
  readonly quota = this.quotaSignal.asReadonly();

  /**
   * Whether this login's own live-stream budget was full the last time we asked. False until a
   * stream has actually been refused — this is never a preflight check, only an explanation after
   * the fact.
   */
  readonly perSubscriberLimitReached = computed(
    () => this.quotaSignal()?.perSubscriberLimitReached ?? false,
  );

  constructor() {
    // One probe per fatal close, and only after one: asking before anything failed would spend a
    // request on every page load to learn a number nobody needs while the stream is up.
    effect(() => {
      if (this.liveUpdate.fatalCloseCount() === 0) {
        return;
      }
      this.probe();
    });

    // Self-clearing, and it has to be: the budget frees itself the moment a tab closes, so a hint
    // that outlived the condition would send people hunting for tabs they already shut. A stream
    // reaching 'open' is proof there was room for it.
    effect(() => {
      if (this.liveUpdate.status() === 'open') {
        this.probeGeneration++;
        this.quotaSignal.set(null);
      }
    });
  }

  private probe(): void {
    const generation = this.probeGeneration;
    this.http.get<LiveStreamStatus>('/api/live/status').subscribe({
      next: (status) => {
        if (generation !== this.probeGeneration) {
          return;
        }
        this.quotaSignal.set(status);
      },
      // A hint that cannot be substantiated is not shown. The failure this whole path exists to
      // explain is itself a sign of a wobbly connection, and guessing "it was probably your tabs"
      // would be the same unfounded claim as today's silence, only louder. No generation check on
      // this side: the effect above has already set false, and setting it again is the same value.
      error: () => this.quotaSignal.set(null),
    });
  }
}
