import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { AllowedRoles, VoteType } from './vote-session.model';
import { VoteSessionService } from './vote-session.service';

describe('VoteSessionService', () => {
  let service: VoteSessionService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(VoteSessionService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('list GETs the channel vote-sessions with paging params', () => {
    service.list('sensitron', 2, 10).subscribe();

    const req = httpMock.expectOne(
      (r) => r.url === '/api/channels/sensitron/vote-sessions' && r.params.get('page') === '2' && r.params.get('pageSize') === '10',
    );
    expect(req.request.method).toBe('GET');
    req.flush({ items: [], page: 2, pageSize: 10, totalCount: 0 });
  });

  it('list defaults to page 1 / pageSize 20', () => {
    service.list('sensitron').subscribe();

    const req = httpMock.expectOne(
      (r) => r.url === '/api/channels/sensitron/vote-sessions' && r.params.get('page') === '1' && r.params.get('pageSize') === '20',
    );
    req.flush({ items: [], page: 1, pageSize: 20, totalCount: 0 });
  });

  it('delete DELETEs the session', () => {
    service.delete('sensitron', 5).subscribe();

    const req = httpMock.expectOne('/api/channels/sensitron/vote-sessions/5');
    expect(req.request.method).toBe('DELETE');
    req.flush(null);
  });

  it('listMine GETs /api/vote-sessions/mine with paging params', () => {
    service.listMine(3, 15).subscribe();

    const req = httpMock.expectOne(
      (r) => r.url === '/api/vote-sessions/mine' && r.params.get('page') === '3' && r.params.get('pageSize') === '15',
    );
    req.flush({ items: [], page: 3, pageSize: 15, totalCount: 0 });
  });

  it('create POSTs title/allowedVoterRoles, omitting startedAt when not given', () => {
    service.create('sensitron', 'Cleanup round 1', AllowedRoles.Everyone | AllowedRoles.Mods).subscribe();

    const req = httpMock.expectOne('/api/channels/sensitron/vote-sessions');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({
      title: 'Cleanup round 1',
      allowedVoterRoles: AllowedRoles.Everyone | AllowedRoles.Mods,
    });
    req.flush({});
  });

  it('create includes startedAt when given', () => {
    service.create('sensitron', 'Cleanup round 1', AllowedRoles.Everyone, '2026-08-01T00:00:00Z').subscribe();

    const req = httpMock.expectOne('/api/channels/sensitron/vote-sessions');
    expect(req.request.body).toEqual({
      title: 'Cleanup round 1',
      allowedVoterRoles: AllowedRoles.Everyone,
      startedAt: '2026-08-01T00:00:00Z',
    });
    req.flush({});
  });

  it('end POSTs to the /end sub-route', () => {
    service.end('sensitron', 5).subscribe();

    const req = httpMock.expectOne('/api/channels/sensitron/vote-sessions/5/end');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({});
    req.flush({});
  });

  it('getResults GETs the /results sub-route', () => {
    service.getResults('sensitron', 5).subscribe();

    const req = httpMock.expectOne('/api/channels/sensitron/vote-sessions/5/results');
    expect(req.request.method).toBe('GET');
    req.flush({ sessionId: 5, title: 't', isActive: true, startedAt: '', endedAt: null, emotes: [] });
  });

  it('castVote POSTs emoteId and type to the votes sub-route', () => {
    service.castVote('sensitron', 5, 'emote-1', VoteType.Keep).subscribe();

    const req = httpMock.expectOne('/api/channels/sensitron/vote-sessions/5/votes');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ emoteId: 'emote-1', type: VoteType.Keep });
    req.flush({});
  });

  it('retractVote DELETEs the specific emote vote', () => {
    service.retractVote('sensitron', 5, 'emote-1').subscribe();

    const req = httpMock.expectOne('/api/channels/sensitron/vote-sessions/5/votes/emote-1');
    expect(req.request.method).toBe('DELETE');
    req.flush(null);
  });
});
