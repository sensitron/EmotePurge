import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { catchError, map, of, switchMap } from 'rxjs';

import { ChannelService } from './channel.service';
import { AuthService } from '../auth/auth.service';

/**
 * The stricter sibling of `usageStatsAccessGuard`: reads `canManage` rather than
 * `canViewUsageStats`, so it admits admins, the broadcaster and live moderators but **not** the
 * channel's 7TV editors (`ChannelManagementAuthorizationFilter` server-side).
 *
 * Used by the activity feed, whose rows name which moderator did what. Everything else a channel
 * shows is aggregated; this is the one screen where the difference between the two permission
 * levels is about people rather than numbers.
 */
export const channelManageGuard: CanActivateFn = (route, state) => {
  const authService = inject(AuthService);
  const channelService = inject(ChannelService);
  const router = inject(Router);

  const channelName = route.paramMap.get('channelName');
  if (!channelName) {
    return of(router.createUrlTree(['/']));
  }

  // Same fallback as the usage guard: stay inside the channel the user navigated into rather than
  // bouncing them out to the overview.
  const fallback = () => router.createUrlTree(['/channels', channelName, 'vote-sessions']);

  return authService.ensureLoaded().pipe(
    switchMap((user) => {
      if (!user) {
        authService.stashReturnUrl(state.url);
        return of(router.createUrlTree(['/login']));
      }

      return channelService.getPermissions(channelName).pipe(
        map((permissions) => (permissions.canManage ? true : fallback())),
        catchError(() => of(fallback())),
      );
    }),
  );
};
