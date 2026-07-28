import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { catchError, map, of, switchMap } from 'rxjs';

import { AuthService } from '../auth/auth.service';
import { VoteSessionService } from './vote-session.service';

/**
 * Vote-session detail pages used to be intentionally public (anonymous share-link viewing) —
 * removed on request: every viewer must now be logged in AND part of this specific session's
 * target audience (VoteAudienceFilter server-side). Mirrors usageStatsAccessGuard's pattern:
 * calls the real results endpoint and interprets success/failure as the authorization signal,
 * rather than adding a separate "am I allowed" endpoint.
 */
export const voteSessionAccessGuard: CanActivateFn = (route, state) => {
  const authService = inject(AuthService);
  const voteSessionService = inject(VoteSessionService);
  const router = inject(Router);

  const channelName = route.paramMap.get('channelName');
  const sessionId = Number(route.paramMap.get('sessionId'));
  if (!channelName || !Number.isFinite(sessionId)) {
    return of(router.createUrlTree(['/']));
  }

  return authService.ensureLoaded().pipe(
    switchMap((user) => {
      if (!user) {
        authService.stashReturnUrl(state.url);
        return of(router.createUrlTree(['/login']));
      }

      return voteSessionService.getResults(channelName, sessionId).pipe(
        map(() => true),
        catchError(() => of(router.createUrlTree(['/channels', channelName, 'vote-sessions']))),
      );
    }),
  );
};
