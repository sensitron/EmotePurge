import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { catchError, map, of } from 'rxjs';

import { UsageStatService } from '../usage-stats/usage-stat.service';

/**
 * Distinct from `authGuard`: checks that the user can actually view THIS channel's usage stats
 * (`UsageStatsAccessAuthorizationFilter` server-side — admin/broadcaster/live-moderator OR a 7TV
 * editor of the channel), not just "is logged in". Probes the real usage-stats endpoint rather
 * than the full-management `GET /api/channels/{channelName}` check, since 7TV editors must pass
 * this guard without being allowed to manage the channel (join/leave, vote sessions) at all.
 * Probe result (a single day's totals) is discarded — only success/failure counts.
 */
export const usageStatsAccessGuard: CanActivateFn = (route) => {
  const usageStatService = inject(UsageStatService);
  const router = inject(Router);

  const channelName = route.paramMap.get('channelName');
  if (!channelName) {
    return of(router.createUrlTree(['/']));
  }

  const today = new Date().toISOString().slice(0, 10);

  return usageStatService.getTotals(channelName, today, today).pipe(
    map(() => true),
    catchError(() => of(router.createUrlTree(['/channels', channelName, 'vote-sessions']))),
  );
};
