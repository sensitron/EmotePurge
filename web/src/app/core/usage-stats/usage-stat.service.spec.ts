import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

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
});
