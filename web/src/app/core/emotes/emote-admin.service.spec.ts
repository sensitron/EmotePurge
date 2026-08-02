import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { EmoteAdminService } from './emote-admin.service';
import { EmoteSetStatus } from './emote-set-status.model';

describe('EmoteAdminService', () => {
  let service: EmoteAdminService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(EmoteAdminService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('syncDeleted POSTs emoteIds to the sync-deleted endpoint', () => {
    service.syncDeleted('sensitron', ['a', 'b']).subscribe();

    const req = httpMock.expectOne('/api/channels/sensitron/emotes/sync-deleted');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ emoteIds: ['a', 'b'] });
    req.flush({ archivedCount: 2, notFoundIds: [] });
  });

  it('syncRestored POSTs emoteIds to the sync-restored endpoint', () => {
    service.syncRestored('sensitron', ['a', 'b']).subscribe();

    const req = httpMock.expectOne('/api/channels/sensitron/emotes/sync-restored');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ emoteIds: ['a', 'b'] });
    req.flush({ restoredCount: 2, notFoundIds: [] });
  });

  it('getSetWarning GETs the set-warning endpoint', () => {
    service.getSetWarning('sensitron').subscribe();

    const req = httpMock.expectOne('/api/channels/sensitron/emotes/set-warning');
    expect(req.request.method).toBe('GET');
    req.flush({
      available: true,
      isOwnSet: true,
      otherTrackedChannelsSharingSet: [],
      otherModeratedChannelsSharingSet: [],
    });
  });

  it('getSetStatus GETs the active-set endpoint', () => {
    let status: EmoteSetStatus | undefined;
    service.getSetStatus('sensitron').subscribe((value) => (status = value));

    const req = httpMock.expectOne('/api/channels/sensitron/emotes/active-set');
    expect(req.request.method).toBe('GET');
    req.flush({
      activeEmoteSetId: 'set-1',
      capacity: 1000,
      occupiedSlots: 847,
      trackedSince: '2026-06-12T09:14:00Z',
    });

    expect(status).toEqual({
      activeEmoteSetId: 'set-1',
      capacity: 1000,
      occupiedSlots: 847,
      trackedSince: '2026-06-12T09:14:00Z',
    });
  });
});
