import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { ChannelAuditService } from './channel-audit.service';
import { AuditLogEntry } from '../audit/audit.model';
import { PagedResult } from '../models/paged-result.model';

const EMPTY_PAGE: PagedResult<AuditLogEntry> = {
  items: [],
  page: 1,
  pageSize: 25,
  totalCount: 0,
  totalPages: 0,
};

describe('ChannelAuditService', () => {
  let service: ChannelAuditService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(ChannelAuditService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('GETs the channel-scoped route with paging params', () => {
    service.listAuditLog('handofblood', 2, 25).subscribe();

    const req = httpMock.expectOne('/api/channels/handofblood/audit-log?page=2&pageSize=25');
    expect(req.request.method).toBe('GET');
    req.flush(EMPTY_PAGE);
  });

  it('defaults to the first page with pageSize 25', () => {
    service.listAuditLog('handofblood').subscribe();

    httpMock.expectOne('/api/channels/handofblood/audit-log?page=1&pageSize=25').flush(EMPTY_PAGE);
  });

  it('passes action and actor filters through', () => {
    service
      .listAuditLog('handofblood', 1, 25, { action: 'voteSession.delete', actor: 'sensi' })
      .subscribe();

    httpMock
      .expectOne(
        '/api/channels/handofblood/audit-log?page=1&pageSize=25&action=voteSession.delete&actor=sensi',
      )
      .flush(EMPTY_PAGE);
  });

  it('omits empty filters instead of sending blank parameters', () => {
    service.listAuditLog('handofblood', 1, 25, { action: '', actor: undefined }).subscribe();

    httpMock.expectOne('/api/channels/handofblood/audit-log?page=1&pageSize=25').flush(EMPTY_PAGE);
  });

  it('never sends a channel query parameter', () => {
    // The server takes the channel from the route and ignores anything else — but a client that
    // sent one would suggest it is a filter, which is exactly the confusion that could turn into a
    // cross-channel read the next time someone touches the handler.
    service.listAuditLog('handofblood', 1, 25, { actor: 'sensi' }).subscribe();

    const req = httpMock.expectOne((r) => r.url === '/api/channels/handofblood/audit-log');
    expect(req.request.params.has('channel')).toBe(false);
    req.flush(EMPTY_PAGE);
  });
});
