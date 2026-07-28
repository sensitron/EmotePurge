import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { EmoteAdminService } from './emote-admin.service';

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

  it('getSetWarning GETs the set-warning endpoint', () => {
    service.getSetWarning('sensitron').subscribe();

    const req = httpMock.expectOne('/api/channels/sensitron/emotes/set-warning');
    expect(req.request.method).toBe('GET');
    req.flush({ available: true, isOwnSet: true, otherTrackedChannelsSharingSet: [], otherModeratedChannelsSharingSet: [] });
  });

  it('getActiveEmoteSetId GETs the active-set endpoint', () => {
    service.getActiveEmoteSetId('sensitron').subscribe();

    const req = httpMock.expectOne('/api/channels/sensitron/emotes/active-set');
    expect(req.request.method).toBe('GET');
    req.flush({ activeEmoteSetId: 'set-1' });
  });
});
