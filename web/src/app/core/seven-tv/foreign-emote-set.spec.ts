import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { ForeignEmoteSetResponse } from './foreign-emote-set.model';
import { ForeignEmoteSetService } from './foreign-emote-set.service';

function response(overrides: Partial<ForeignEmoteSetResponse> = {}): ForeignEmoteSetResponse {
  return {
    channelName: 'handofblood',
    sevenTvUserId: 'user1',
    emoteSetId: 'set1',
    totalCount: 1,
    truncated: false,
    emotes: [
      {
        sevenTvEmoteId: 'e1',
        name: 'catJAM',
        defaultName: 'catJAM',
        imageUrl: 'https://cdn.7tv.app/e1/4x.webp',
        topAllTime: 12,
        trending: null,
      },
    ],
    ...overrides,
  };
}

describe('ForeignEmoteSetService', () => {
  let service: ForeignEmoteSetService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(ForeignEmoteSetService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('GETs the normalized channel name with no query string by default', () => {
    let result: ForeignEmoteSetResponse | undefined;
    service.load('HandOfBlood').subscribe((r) => (result = r));

    const req = httpMock.expectOne('/api/seventv/channels/handofblood/emotes');
    expect(req.request.method).toBe('GET');
    req.flush(response());

    expect(result).toEqual(response());
  });

  it('appends refresh=true when asked to bypass the cache', () => {
    service.load('handofblood', { refresh: true }).subscribe();

    const req = httpMock.expectOne(
      (candidate) =>
        candidate.url === '/api/seventv/channels/handofblood/emotes' &&
        candidate.params.get('refresh') === 'true',
    );
    req.flush(response());
  });

  it('omits refresh from the query string when not requested', () => {
    service.load('handofblood', { refresh: false }).subscribe();

    const req = httpMock.expectOne('/api/seventv/channels/handofblood/emotes');
    expect(req.request.params.has('refresh')).toBe(false);
    req.flush(response());
  });
});
