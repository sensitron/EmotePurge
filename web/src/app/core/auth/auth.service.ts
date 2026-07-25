import { HttpClient } from '@angular/common/http';
import { inject, Injectable, signal } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, Observable, of, tap } from 'rxjs';

import { AuthUser } from './auth.model';

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);

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

  /** Full browser navigation, not an HttpClient call — Twitch OAuth needs a real redirect. */
  login(): void {
    window.location.href = '/api/auth/twitch/login';
  }

  logout(): void {
    this.http.post('/api/auth/logout', {}).subscribe(() => {
      this.currentUser.set(null);
      this.isLoaded.set(true);
      this.router.navigateByUrl('/login');
    });
  }

  /** Called when a request 401s mid-session (cookie expired) — resets state and sends the user back to /login. */
  handleSessionExpired(): void {
    this.currentUser.set(null);
    this.isLoaded.set(true);
    this.router.navigateByUrl('/login');
  }
}
