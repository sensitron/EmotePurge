import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { EmoteUsageSeries } from './usage-stat.model';
import { UsageStatService } from './usage-stat.service';

describe('UsageStatService', () => {
  let service: UsageStatService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(UsageStatService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('getTotals GETs the totals endpoint with from/to query params', () => {
    service.getTotals('sensitron', '2026-07-01', '2026-07-28').subscribe();

    const req = httpMock.expectOne(
      (r) =>
        r.url === '/api/channels/sensitron/usage-stats/totals' &&
        r.params.get('from') === '2026-07-01' &&
        r.params.get('to') === '2026-07-28',
    );
    expect(req.request.method).toBe('GET');
    req.flush([]);
  });

  const SERIES: EmoteUsageSeries = {
    emoteId: 'e1',
    emoteName: 'PogU',
    from: '2026-07-01',
    to: '2026-07-28',
    totalUseCount: 0,
    firstUsedDate: null,
    lastUsedDate: null,
    days: [],
  };

  it('getDailySeries GETs the daily endpoint with emoteId/from/to query params', () => {
    service.getDailySeries('sensitron', 'e1', '2026-07-01', '2026-07-28').subscribe();

    const req = httpMock.expectOne(
      (r) =>
        r.url === '/api/channels/sensitron/usage-stats/daily' &&
        r.params.get('emoteId') === 'e1' &&
        r.params.get('from') === '2026-07-01' &&
        r.params.get('to') === '2026-07-28',
    );
    expect(req.request.method).toBe('GET');
    req.flush(SERIES);
  });

  it('serves a second identical getDailySeries call from the cache', () => {
    service.getDailySeries('sensitron', 'e1', '2026-07-01', '2026-07-28').subscribe();
    httpMock.expectOne((r) => r.url === '/api/channels/sensitron/usage-stats/daily').flush(SERIES);

    let replayed: EmoteUsageSeries | undefined;
    service
      .getDailySeries('sensitron', 'e1', '2026-07-01', '2026-07-28')
      .subscribe((series) => (replayed = series));

    // No second request — httpMock.verify() in afterEach would flag one.
    expect(replayed?.emoteName).toBe('PogU');
  });

  it('clearSeriesCache forces a fresh request', () => {
    service.getDailySeries('sensitron', 'e1', '2026-07-01', '2026-07-28').subscribe();
    httpMock.expectOne((r) => r.url === '/api/channels/sensitron/usage-stats/daily').flush(SERIES);

    service.clearSeriesCache();
    service.getDailySeries('sensitron', 'e1', '2026-07-01', '2026-07-28').subscribe();
    httpMock.expectOne((r) => r.url === '/api/channels/sensitron/usage-stats/daily').flush(SERIES);
  });

  it('does not cache a failed request', () => {
    service.getDailySeries('sensitron', 'e1', '2026-07-01', '2026-07-28').subscribe({
      error: () => undefined,
    });
    httpMock
      .expectOne((r) => r.url === '/api/channels/sensitron/usage-stats/daily')
      .flush({ errorCode: 'unexpected_error' }, { status: 500, statusText: 'Server Error' });

    service.getDailySeries('sensitron', 'e1', '2026-07-01', '2026-07-28').subscribe();
    httpMock.expectOne((r) => r.url === '/api/channels/sensitron/usage-stats/daily').flush(SERIES);
  });
});
