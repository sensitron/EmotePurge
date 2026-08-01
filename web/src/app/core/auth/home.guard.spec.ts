import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import {
  ActivatedRouteSnapshot,
  Router,
  RouterStateSnapshot,
  UrlTree,
  provideRouter,
} from '@angular/router';
import { Observable, firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { AuthService } from './auth.service';
import { homeGuard } from './home.guard';

/**
 * The distinction this guard exists for: on '/' an anonymous visitor is a *prospect* and belongs on
 * the marketing page, while authGuard treats an anonymous visitor on a deep link as someone who
 * needs to log in. Sending everyone to /login would have hidden the landing page behind a form.
 */
describe('homeGuard', () => {
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

  afterEach(() => httpMock.verify());

  function runGuard(): Observable<boolean | UrlTree> {
    return TestBed.runInInjectionContext(() =>
      homeGuard({} as ActivatedRouteSnapshot, { url: '/' } as RouterStateSnapshot),
    ) as Observable<boolean | UrlTree>;
  }

  it('lets a logged-in visitor through to the overview', async () => {
    const result = firstValueFrom(runGuard());
    httpMock.expectOne('/api/auth/me').flush({
      twitchUserId: '1',
      twitchLogin: 'sensitron',
      displayName: 'Sensitron',
      isGlobalAdmin: false,
    });

    expect(await result).toBe(true);
  });

  it('redirects an anonymous visitor to /welcome, not to /login', async () => {
    const result = firstValueFrom(runGuard());
    httpMock.expectOne('/api/auth/me').flush(null, { status: 401, statusText: 'Unauthorized' });

    const outcome = await result;
    expect(outcome).toBeInstanceOf(UrlTree);
    expect(router.serializeUrl(outcome as UrlTree)).toBe('/welcome');
  });

  it('resolves the session once and reuses it on a second run', async () => {
    const first = firstValueFrom(runGuard());
    httpMock.expectOne('/api/auth/me').flush({
      twitchUserId: '1',
      twitchLogin: 'sensitron',
      displayName: 'Sensitron',
      isGlobalAdmin: false,
    });
    await first;

    // No second /api/auth/me — ensureLoaded() caches, and afterEach's verify() would fail otherwise.
    expect(await firstValueFrom(runGuard())).toBe(true);
  });
});
