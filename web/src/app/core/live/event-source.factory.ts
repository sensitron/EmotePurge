import { InjectionToken } from '@angular/core';

export type EventSourceFactory = (url: string) => EventSource;

/**
 * Builds the browser's `EventSource`. This token exists purely for testability: `EventSource` is a
 * separate transport that bypasses `HttpClient` entirely, so neither `apiAuthInterceptor` nor
 * `HttpTestingController` can see it — without an injectable seam there would be no way to drive
 * LiveUpdateService from Vitest (jsdom does not implement `EventSource` at all).
 *
 * No `withCredentials`: every call is same-origin (dev proxy in `ng serve`, wwwroot in prod), so the
 * session cookie is sent automatically — same reasoning as for every `HttpClient` call in the app.
 */
export const EVENT_SOURCE_FACTORY = new InjectionToken<EventSourceFactory>('EVENT_SOURCE_FACTORY', {
  providedIn: 'root',
  factory: () => (url: string) => new EventSource(url),
});
