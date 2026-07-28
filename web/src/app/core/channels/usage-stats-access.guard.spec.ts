import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { convertToParamMap, provideRouter, Router, UrlTree } from '@angular/router';
import { firstValueFrom, Observable } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { usageStatsAccessGuard } from './usage-stats-access.guard';

describe('usageStatsAccessGuard', () => {
  let httpMock: HttpTestingController;
  let router: Router;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    httpMock = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
  });

  afterEach(() => {
    httpMock.verify();
  });

  function runGuard(channelName: string | null): Observable<boolean | UrlTree> {
    return TestBed.runInInjectionContext(() =>
      usageStatsAccessGuard(
        { paramMap: convertToParamMap(channelName ? { channelName } : {}) } as never,
        {} as never,
      ),
    ) as Observable<boolean | UrlTree>;
  }

  it('redirects to "/" when the route has no channelName', async () => {
    const result = await firstValueFrom(runGuard(null));

    expect(result).toBeInstanceOf(UrlTree);
    expect(router.serializeUrl(result as UrlTree)).toBe('/');
    httpMock.expectNone(() => true);
  });

  it('allows navigation when the totals probe succeeds', async () => {
    const resultPromise = firstValueFrom(runGuard('sensitron'));

    httpMock.expectOne((r) => r.url === '/api/channels/sensitron/usage-stats/totals').flush([]);

    expect(await resultPromise).toBe(true);
  });

  it('redirects to the channel vote-sessions list when the probe is rejected', async () => {
    const resultPromise = firstValueFrom(runGuard('sensitron'));

    httpMock
      .expectOne((r) => r.url === '/api/channels/sensitron/usage-stats/totals')
      .flush(null, { status: 403, statusText: 'Forbidden' });

    const result = await resultPromise;
    expect(result).toBeInstanceOf(UrlTree);
    expect(router.serializeUrl(result as UrlTree)).toBe('/channels/sensitron/vote-sessions');
  });
});
