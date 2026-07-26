import { HttpClient } from '@angular/common/http';
import { inject, Injectable, signal } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, Observable, of, tap } from 'rxjs';

import { SevenTvTokenService } from '../seven-tv/seven-tv-token.service';
import { AuthUser } from './auth.model';

const RETURN_URL_STORAGE_KEY = 'ep_return_url';

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);
  // Unrelated token (see seven-tv-token.service.ts), cleared here anyway as cheap hygiene —
  // no reason for a 7TV write-token to outlive the EmotePurge session that granted access to it.
  private readonly sevenTvTokenService = inject(SevenTvTokenService);

  readonly currentUser = signal<AuthUser | null>(null);
  private readonly isLoaded = signal(false);

  /** Fetches /api/auth/me once and caches the result in `currentUser` until logout/401. */
  ensureLoaded(): Observable<AuthUser | null> {
    if (this.isLoaded()) {
      return of(this.currentUser());
    }

    return this.http.get<AuthUser>('/api/auth/me').pipe(
      catchError(() => of(null)),
      tap((user) => {
        this.currentUser.set(user);
        this.isLoaded.set(true);
      }),
    );
  }

  /**
   * Full browser navigation, not an HttpClient call — Twitch OAuth needs a real redirect.
   * `returnUrl` is stashed so an anonymous share-link visitor (e.g. a vote-session page) lands
   * back where they started instead of the fixed post-login redirect the backend always uses.
   */
  login(returnUrl?: string): void {
    if (returnUrl) {
      sessionStorage.setItem(RETURN_URL_STORAGE_KEY, returnUrl);
    }
    window.location.href = '/api/auth/twitch/login';
  }

  /** Reads and clears the stashed return URL, if any — consumed once by AppShell after login. */
  consumeReturnUrl(): string | null {
    const returnUrl = sessionStorage.getItem(RETURN_URL_STORAGE_KEY);
    if (returnUrl) {
      sessionStorage.removeItem(RETURN_URL_STORAGE_KEY);
    }
    return returnUrl;
  }

  logout(): void {
    this.http.post('/api/auth/logout', {}).subscribe(() => {
      this.currentUser.set(null);
      this.isLoaded.set(true);
      this.sevenTvTokenService.clearToken();
      this.router.navigateByUrl('/login');
    });
  }

  /** Called when a request 401s mid-session (cookie expired) — resets state and sends the user back to /login. */
  handleSessionExpired(): void {
    this.currentUser.set(null);
    this.isLoaded.set(true);
    this.sevenTvTokenService.clearToken();
    this.router.navigateByUrl('/login');
  }
}
