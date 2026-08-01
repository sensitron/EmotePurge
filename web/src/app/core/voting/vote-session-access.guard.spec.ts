import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { convertToParamMap, provideRouter, Router, UrlTree } from '@angular/router';
import { firstValueFrom, Observable } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { AuthService } from '../auth/auth.service';
import { voteSessionAccessGuard } from './vote-session-access.guard';

const USER = {
  twitchUserId: '1',
  login: 'sensitron',
  displayName: 'Sensitron',
  tokenExpiresAtUtc: '2026-07-28T00:00:00Z',
};

describe('voteSessionAccessGuard', () => {
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

  function runGuard(
    channelName: string | null,
    sessionId: string | null,
    url = '/channels/sensitron/vote-sessions/5',
  ): Observable<boolean | UrlTree> {
    return TestBed.runInInjectionContext(() =>
      voteSessionAccessGuard(
        {
          paramMap: convertToParamMap({
            ...(channelName ? { channelName } : {}),
            ...(sessionId ? { sessionId } : {}),
          }),
        } as never,
        { url } as never,
      ),
    ) as Observable<boolean | UrlTree>;
  }

  it('redirects to "/" when channelName or sessionId is missing/invalid', async () => {
    const result = await firstValueFrom(runGuard(null, '5'));

    expect(result).toBeInstanceOf(UrlTree);
    expect(router.serializeUrl(result as UrlTree)).toBe('/');
    httpMock.expectNone(() => true);
  });

  it('redirects to "/" when sessionId is not numeric', async () => {
    const result = await firstValueFrom(runGuard('sensitron', 'abc'));

    expect(result).toBeInstanceOf(UrlTree);
    expect(router.serializeUrl(result as UrlTree)).toBe('/');
  });

  it('stashes the URL and redirects to /login when logged out', async () => {
    const resultPromise = firstValueFrom(runGuard('sensitron', '5'));

    httpMock.expectOne('/api/auth/me').flush(null, { status: 401, statusText: 'Unauthorized' });

    const result = await resultPromise;
    expect(result).toBeInstanceOf(UrlTree);
    expect(router.serializeUrl(result as UrlTree)).toBe('/login');
    expect(sessionStorage.getItem('ep_return_url')).toBe('/channels/sensitron/vote-sessions/5');
  });

  it('allows navigation when logged in and the results probe succeeds', async () => {
    const resultPromise = firstValueFrom(runGuard('sensitron', '5'));

    httpMock.expectOne('/api/auth/me').flush(USER);
    httpMock.expectOne('/api/channels/sensitron/vote-sessions/5/results').flush({
      sessionId: 5,
      title: 't',
      isActive: true,
      startedAt: '',
      endedAt: null,
      emotes: [],
    });

    expect(await resultPromise).toBe(true);
  });

  it('redirects to the vote-sessions list when logged in but not part of the audience', async () => {
    const resultPromise = firstValueFrom(runGuard('sensitron', '5'));

    httpMock.expectOne('/api/auth/me').flush(USER);
    httpMock
      .expectOne('/api/channels/sensitron/vote-sessions/5/results')
      .flush(null, { status: 403, statusText: 'Forbidden' });

    const result = await resultPromise;
    expect(result).toBeInstanceOf(UrlTree);
    expect(router.serializeUrl(result as UrlTree)).toBe('/channels/sensitron/vote-sessions');
  });
});
