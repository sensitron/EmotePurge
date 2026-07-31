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

import { adminGuard } from './admin.guard';
import { AuthService } from './auth.service';

const BASE_USER = {
  twitchUserId: '1',
  login: 'sensitron',
  displayName: 'Sensitron',
  tokenExpiresAtUtc: '2026-07-28T00:00:00Z',
};

describe('adminGuard', () => {
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
      adminGuard({} as ActivatedRouteSnapshot, { url } as RouterStateSnapshot),
    ) as Observable<boolean | UrlTree>;
  }

  it('allows navigation for a global admin', async () => {
    const resultPromise = firstValueFrom(runGuard('/admin/monitoring'));

    httpMock.expectOne('/api/auth/me').flush({ ...BASE_USER, isGlobalAdmin: true });

    expect(await resultPromise).toBe(true);
  });

  it('redirects a logged-in non-admin to the overview without stashing a return URL', async () => {
    const resultPromise = firstValueFrom(runGuard('/admin/monitoring'));

    httpMock.expectOne('/api/auth/me').flush({ ...BASE_USER, isGlobalAdmin: false });

    const result = await resultPromise;
    expect(result).toBeInstanceOf(UrlTree);
    expect(router.serializeUrl(result as UrlTree)).toBe('/');
    // Deliberately no stash: the visitor is not missing a session, only the admin role — sending
    // them through /login would just loop them back here.
    expect(sessionStorage.getItem('ep_return_url')).toBeNull();
  });

  it('redirects to the overview (not /login) when logged out', async () => {
    const resultPromise = firstValueFrom(runGuard('/admin/monitoring'));

    httpMock.expectOne('/api/auth/me').flush(null, { status: 401, statusText: 'Unauthorized' });

    const result = await resultPromise;
    expect(result).toBeInstanceOf(UrlTree);
    expect(router.serializeUrl(result as UrlTree)).toBe('/');
  });
});
