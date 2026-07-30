import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { catchError, map, of, switchMap } from 'rxjs';

import { AuthService } from '../auth/auth.service';
import { ChannelService } from './channel.service';

/**
 * Distinct from `authGuard`: checks that the user can actually view THIS channel's usage stats
 * (`UsageStatsAccessAuthorizationFilter` server-side — admin/broadcaster/live-moderator OR a 7TV
 * editor of the channel), not just "is logged in".
 *
 * Reads `canViewUsageStats` from the permissions endpoint. It used to probe the real usage-stats
 * endpoint for a single day and read success/failure as the answer — a full aggregate query per
 * navigation, thrown away, and for a logged-out visitor a 401 racing the interceptor's own redirect.
 * Login is settled first here, so that 401 can no longer happen.
 */
export const usageStatsAccessGuard: CanActivateFn = (route, state) => {
  const authService = inject(AuthService);
  const channelService = inject(ChannelService);
  const router = inject(Router);

  const channelName = route.paramMap.get('channelName');
  if (!channelName) {
    return of(router.createUrlTree(['/']));
  }

  // Redirects to the channel's own voting page rather than the global overview, so someone without
  // usage access stays in the context they navigated into.
  const fallback = () => router.createUrlTree(['/channels', channelName, 'vote-sessions']);

  return authService.ensureLoaded().pipe(
    switchMap((user) => {
      if (!user) {
        authService.stashReturnUrl(state.url);
        return of(router.createUrlTree(['/login']));
      }

      return channelService.getPermissions(channelName).pipe(
        map((permissions) => (permissions.canViewUsageStats ? true : fallback())),
        catchError(() => of(fallback())),
      );
    }),
  );
};
