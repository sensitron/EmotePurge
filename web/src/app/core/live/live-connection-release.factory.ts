import { InjectionToken } from '@angular/core';

export type LiveConnectionReleaseFactory = (connectionId: string) => void;

/** Builds the URL for releasing one SSE connection's slot — see LIVE_CONNECTION_RELEASE_FACTORY. */
export function liveConnectionReleaseUrl(connectionId: string): string {
  return `/api/live/connections/${connectionId}`;
}

/**
 * Fires the fire-and-forget `DELETE /api/live/connections/{id}` that frees an SSE slot the instant
 * this tab closes a stream on purpose (issue #128 follow-up).
 *
 * Behind Cloudflare, a browser-initiated `EventSource.close()` reaches the origin only 15–25 s
 * later, and until it does the server still counts the slot as held — quick navigation between
 * channels was filling the per-login quota (6 streams) and producing 429s. The endpoint always
 * answers 204 for a logged-in caller, including for an unknown or already-ended connection id, so
 * this call never needs to inspect the response.
 *
 * Deliberately not `HttpClient`: the app-wide `apiAuthInterceptor` would turn a 401 here into a
 * redirect to `/login` in the middle of an ordinary navigation, and `HttpClient`'s default backend
 * has no `keepalive` option — without it, a request fired from `pagehide` can be cancelled by the
 * browser before it reaches the network. This mirrors why `EVENT_SOURCE_FACTORY` exists: SSE/its
 * teardown bypass `HttpClient` on purpose, and this token is what makes the release call stubbable
 * in Vitest without a real network.
 *
 * Fire-and-forget by design: nothing in {@link LiveUpdateService} awaits this, and every failure —
 * a rejected fetch, an offline tab, a request cut short by navigation — is swallowed here. The
 * slot frees itself server-side on its own timeout regardless, so there is nothing useful to retry
 * or surface to the user.
 */
export const LIVE_CONNECTION_RELEASE_FACTORY = new InjectionToken<LiveConnectionReleaseFactory>(
  'LIVE_CONNECTION_RELEASE_FACTORY',
  {
    providedIn: 'root',
    factory:
      () =>
      (connectionId: string): void => {
        fetch(liveConnectionReleaseUrl(connectionId), {
          method: 'DELETE',
          keepalive: true,
          credentials: 'same-origin',
        }).catch(() => {
          // Fire-and-forget — see the class doc for why nothing here reacts to a failure.
        });
      },
  },
);
