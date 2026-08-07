import { MonoTypeOperatorFunction, Observable } from 'rxjs';

/**
 * Drops the answer of any request that a later one has already superseded — "last asked wins",
 * not "last answered wins".
 *
 * One guard instance stands for one readout: every subscription it is piped through takes a
 * generation number, and only the highest one may still deliver. Pipe two independent readouts
 * through two separate instances, or they will cancel each other out.
 *
 * Why this and not `switchMap`: the callers issue their requests from effects and event handlers,
 * not from a single stream they could switch on, and the underlying `HttpClient` observables are
 * cached with `shareReplay({ refCount: false })` — unsubscribing would not cancel them anyway.
 * Discarding the stale answer is therefore the whole of the job.
 *
 * A superseded stream completes silently rather than passing its value or its error on: the error
 * of a request nobody is waiting for any more must not raise an error state over current data.
 */
export function latestOnly<T>(): MonoTypeOperatorFunction<T> {
  let latest = 0;

  return (source) =>
    new Observable<T>((subscriber) => {
      // Claimed on subscribe, not on creation, so the order matches the order the requests went out.
      const generation = ++latest;
      const isCurrent = () => generation === latest;

      return source.subscribe({
        next: (value) => {
          if (isCurrent()) {
            subscriber.next(value);
          }
        },
        error: (error: unknown) => {
          if (isCurrent()) {
            subscriber.error(error);
          } else {
            subscriber.complete();
          }
        },
        complete: () => subscriber.complete(),
      });
    });
}
