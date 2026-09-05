import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { tap } from 'rxjs';

import { AuthService } from '../auth/auth.service';

/**
 * Requests that legitimately answer 401 without the EmotePurge session being gone.
 * `auth/me` is the probe every guard runs first and 401s for every anonymous visitor — treating
 * that as an expiry would redirect the public landing page to /login. `auth/logout` 401s when the
 * cookie is already gone, which is the outcome the caller wanted anyway.
 * `live/status` is the diagnostic LiveQuotaService runs *after* a stream was refused, and a revoked
 * session is one of the reasons a stream is refused — so without this exception every fatal SSE
 * close would turn into an immediate redirect to /login, which is not what it did before the probe
 * existed. An expired session still surfaces on the next real request, exactly as it does today.
 */
const EXPECTED_401_PATHS = ['/api/auth/me', '/api/auth/logout', '/api/live/status'];

/**
 * Centralizes "the session expired" for the whole app. Before this, six pages carried the same
 * `if (error.status === 401) { handleSessionExpired(); return; }` block and a seventh
 * (channel-workspace-layout) was missing it entirely.
 *
 * The `/api/` restriction is the load-bearing part: the 7TV mass-delete engine talks straight to
 * `7tv.io/v3/gql` with a completely different credential (the user's 7TV write token from
 * sessionStorage). A 401 from there means "that token expired" and is handled by
 * SevenTvDeleteService — it must never log the user out of EmotePurge. That distinction used to
 * exist only implicitly, in the fact that nobody had copied the 401 block into the 7TV service.
 *
 * Errors are re-thrown, not swallowed: callers still need to stop their spinners and render a
 * message (see apiErrorTranslationKey).
 */
export const apiAuthInterceptor: HttpInterceptorFn = (req, next) => {
  if (!req.url.startsWith('/api/')) {
    return next(req);
  }

  const authService = inject(AuthService);

  return next(req).pipe(
    tap({
      error: (error: unknown) => {
        if (
          error instanceof HttpErrorResponse &&
          error.status === 401 &&
          !EXPECTED_401_PATHS.some((path) => req.url.startsWith(path))
        ) {
          authService.handleSessionExpired();
        }
      },
    }),
  );
};
