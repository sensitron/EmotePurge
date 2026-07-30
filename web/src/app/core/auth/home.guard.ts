import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { map } from 'rxjs';

import { AuthService } from './auth.service';

/**
 * Guards the root route ('/') only. Logged-in visitors see the overview as usual; anonymous
 * visitors land on the public marketing page (/welcome) instead of the login form — unlike
 * authGuard (still used for deep links such as my-votings/channel-workspace routes), where sending
 * an anonymous visitor straight to /login is the more useful behavior.
 */
export const homeGuard: CanActivateFn = () => {
  const authService = inject(AuthService);
  const router = inject(Router);

  return authService
    .ensureLoaded()
    .pipe(map((user) => (user ? true : router.createUrlTree(['/welcome']))));
};
