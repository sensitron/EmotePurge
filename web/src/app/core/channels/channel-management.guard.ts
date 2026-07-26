import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { catchError, map, of } from 'rxjs';

import { ChannelService } from './channel.service';

/**
 * Distinct from `authGuard`: checks that the user can actually manage THIS channel
 * (ChannelManagementAuthorizationFilter server-side — admin/broadcaster/live-moderator), not just
 * "is logged in". Usage-stats is a management tool; unlike the vote-session pages (intentionally
 * public/shareable), it must not be reachable by an unrelated logged-in user or an anonymous visitor.
 * Redirects failures to the channel's (public) vote-sessions page rather than the global overview —
 * keeps the visitor in context instead of bouncing them away entirely.
 */
export const channelManagementGuard: CanActivateFn = (route) => {
  const channelService = inject(ChannelService);
  const router = inject(Router);

  const channelName = route.paramMap.get('channelName');
  if (!channelName) {
    return of(router.createUrlTree(['/']));
  }

  return channelService.getStatus(channelName).pipe(
    map(() => true),
    catchError(() => of(router.createUrlTree(['/channels', channelName, 'vote-sessions']))),
  );
};
