import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import {
  ActivatedRouteSnapshot,
  provideRouter,
  Router,
  RouterStateSnapshot,
  UrlTree,
} from '@angular/router';
import { firstValueFrom, Observable } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { authGuard } from './auth.guard';
import { AuthService } from './auth.service';

describe('authGuard', () => {
  let httpMock: HttpTestingController;
  let router: Router;

  beforeEach(() => {
    sessionStorage.clear();
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    httpMock = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
    TestBed.inject(AuthService);
  });

  afterEach(() => {
    httpMock.verify();
  });

  function runGuard(url: string): Observable<boolean | UrlTree> {
    return TestBed.runInInjectionContext(() =>
      authGuard({} as ActivatedRouteSnapshot, { url } as RouterStateSnapshot),
    ) as Observable<boolean | UrlTree>;
  }

  it('allows navigation when the user is logged in', async () => {
    const resultPromise = firstValueFrom(runGuard('/some-route'));

    httpMock.expectOne('/api/auth/me').flush({
      twitchUserId: '1',
      login: 'sensitron',
      displayName: 'Sensitron',
      tokenExpiresAtUtc: '2026-07-28T00:00:00Z',
      isGlobalAdmin: false,
    });

    expect(await resultPromise).toBe(true);
  });

  it('stashes the attempted URL and redirects to /login when logged out', async () => {
    const resultPromise = firstValueFrom(runGuard('/channels/sensitron/vote-sessions'));

    httpMock.expectOne('/api/auth/me').flush(null, { status: 401, statusText: 'Unauthorized' });

    const result = await resultPromise;
    expect(result).toBeInstanceOf(UrlTree);
    expect(router.serializeUrl(result as UrlTree)).toBe('/login');
    expect(sessionStorage.getItem('ep_return_url')).toBe('/channels/sensitron/vote-sessions');
  });
});
