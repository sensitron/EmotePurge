import { DestroyRef, Signal, inject } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { Observable, debounceTime, defer, filter, map, switchMap, tap } from 'rxjs';

import { LiveEvent } from './live-event.model';
import { LiveUpdateService } from './live-update.service';

/** Event types to react to, or a predicate when the decision needs more than the type (matching a
 *  `sessionId`, for instance). */
export type LiveEventFilter = readonly string[] | ((event: LiveEvent) => boolean);

/**
 * A live-event stream, filtered, scoped to the injecting component's lifetime.
 *
 * Pass a `Signal<string>` for a URL that follows a route parameter: the previous channel's
 * connection is torn down by the `switchMap`, because `LiveUpdateService.stream()` closes on
 * unsubscribe.
 *
 * Use this when the handler needs the individual event (its `channel`, its `sessionId`). When it
 * only needs "something happened, refetch", prefer {@link liveReload} — it collapses bursts.
 *
 * Must be called from an injection context (field initializer or constructor); it takes its own
 * `DestroyRef`, so callers do not add `takeUntilDestroyed()` themselves.
 */
export function liveEvents(
  url: string | Signal<string>,
  accept: LiveEventFilter,
): Observable<LiveEvent> {
  const liveUpdateService = inject(LiveUpdateService);
  const destroyRef = inject(DestroyRef);

  const accepts =
    typeof accept === 'function' ? accept : (event: LiveEvent) => accept.includes(event.type);

  const events =
    typeof url === 'string'
      ? liveUpdateService.stream(url)
      : toObservable(url).pipe(switchMap((current) => liveUpdateService.stream(current)));

  return events.pipe(filter(accepts), takeUntilDestroyed(destroyRef));
}

/**
 * The "listen, then refetch" pipeline the live-updating pages were each building by hand.
 *
 * Emits **once per debounced burst**, carrying the set of event types merged into that burst. That
 * set is what replaces the `syncSeenSinceReload` field two pages used to keep: the reason it existed
 * is that `LiveUpdateService.stream()` is cold — subscribing a second time to inspect the events
 * would open a second `EventSource` — so both pages smuggled the information across the debounce in
 * a mutable field, with the same explanation copy-pasted into both.
 *
 * @example
 * liveReload(this.liveUrl, {
 *   accept: [LIVE_EVENT_TYPES.usageFlushed, LIVE_EVENT_TYPES.channelSynced],
 *   debounceMs: LIVE_RELOAD_DEBOUNCE_MS,
 * }).subscribe((seen) => {
 *   this.reloadQuietly();
 *   if (seen.has(LIVE_EVENT_TYPES.channelSynced)) {
 *     this.refreshActiveEmoteSetId();
 *   }
 * });
 */
export function liveReload(
  url: string | Signal<string>,
  options: { accept: LiveEventFilter; debounceMs: number },
): Observable<ReadonlySet<string>> {
  const events = liveEvents(url, options.accept);

  // defer so each subscription gets its own `seen` set rather than sharing one across subscribers.
  return defer(() => {
    const seen = new Set<string>();
    return events.pipe(
      tap((event) => seen.add(event.type)),
      debounceTime(options.debounceMs),
      map(() => {
        const merged: ReadonlySet<string> = new Set(seen);
        seen.clear();
        return merged;
      }),
    );
  });
}
