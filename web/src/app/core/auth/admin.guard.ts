import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { map } from 'rxjs';

import { AuthService } from './auth.service';

/**
 * Global-admin-only routes. Unlike authGuard this never stashes a return URL and never sends the
 * visitor to /login: a logged-in non-admin is not missing a session, they are simply not an admin,
 * so bouncing them to the overview is the honest outcome. The flag comes from the cached
 * /api/auth/me — no separate probe request, and the server still enforces every admin endpoint.
 */
export const adminGuard: CanActivateFn = () => {
  const authService = inject(AuthService);
  const router = inject(Router);

  return authService
    .ensureLoaded()
    .pipe(map((user) => (user?.isGlobalAdmin ? true : router.createUrlTree(['/']))));
};
