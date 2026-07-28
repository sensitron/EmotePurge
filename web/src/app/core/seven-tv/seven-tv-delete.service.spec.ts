import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { DeleteQueueEmote, SevenTvDeleteService } from './seven-tv-delete.service';
import { SevenTvTokenService } from './seven-tv-token.service';

// Only the keys this service actually translates — not the full app translation file.
const DE_TRANSLATIONS = {
  massDelete: {
    errors: {
      tokenInvalid: 'Token ungültig oder abgelaufen — bitte neues 7TV-Token eintragen.',
      rateLimited: 'Zu viele Anfragen an 7TV (Rate Limit) — später erneut versuchen.',
      networkError: 'Keine Verbindung zu 7TV möglich (Netzwerkfehler).',
      genericStatus: '7TV-Fehler (Status {{ status }}).',
    },
  },
};

const GQL_ENDPOINT = 'https://7tv.io/v3/gql';
const DELETE_DELAY_MS = 275;

const EMOTES: DeleteQueueEmote[] = [
  { emoteId: 'internal-1', sevenTvEmoteId: '7tv-1', name: 'PogU' },
  { emoteId: 'internal-2', sevenTvEmoteId: '7tv-2', name: 'KEKW' },
];

describe('SevenTvDeleteService', () => {
  let service: SevenTvDeleteService;
  let tokenService: SevenTvTokenService;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    sessionStorage.clear();
    vi.useFakeTimers();
    TestBed.configureTestingModule({
      imports: [
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de', 'en'], defaultLang: 'de' },
        }),
      ],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    // Translations load asynchronously even with the synchronous TestingLoader — without this,
    // a translate() call in the same tick as a test's assertions would still see no data loaded
    // yet and fall back to returning the raw key.
    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));
    service = TestBed.inject(SevenTvDeleteService);
    tokenService = TestBed.inject(SevenTvTokenService);
    httpMock = TestBed.inject(HttpTestingController);
    tokenService.setToken('write-token');
  });

  afterEach(() => {
    httpMock.verify();
    vi.useRealTimers();
  });

  it('does nothing without a stored token', () => {
    tokenService.clearToken();

    service.startDelete('set-1', 'sensitron', EMOTES);

    expect(service.isRunning()).toBe(false);
    expect(service.queue()).toEqual([]);
  });

  it('does nothing when the emote list is empty', () => {
    service.startDelete('set-1', 'sensitron', []);

    expect(service.isRunning()).toBe(false);
  });

  it('deletes emotes sequentially with a delay, then syncs the archived ids', () => {
    service.startDelete('set-1', 'sensitron', EMOTES);

    expect(service.isRunning()).toBe(true);
    expect(service.queue().map((i) => i.status)).toEqual(['in-progress', 'pending']);

    const req1 = httpMock.expectOne(GQL_ENDPOINT);
    expect(req1.request.headers.get('Authorization')).toBe('Bearer write-token');
    expect(req1.request.body.variables).toEqual({ setId: 'set-1', emoteId: '7tv-1' });
    req1.flush({});

    expect(service.queue()[0].status).toBe('done');
    expect(service.queue()[1].status).toBe('pending');

    vi.advanceTimersByTime(DELETE_DELAY_MS);
    expect(service.queue()[1].status).toBe('in-progress');

    const req2 = httpMock.expectOne(GQL_ENDPOINT);
    expect(req2.request.body.variables).toEqual({ setId: 'set-1', emoteId: '7tv-2' });
    req2.flush({});

    vi.advanceTimersByTime(DELETE_DELAY_MS);

    expect(service.isRunning()).toBe(false);
    expect(service.progress()).toEqual({ finished: 2, total: 2 });

    const syncReq = httpMock.expectOne('/api/channels/sensitron/emotes/sync-deleted');
    expect(syncReq.request.body).toEqual({ emoteIds: ['internal-1', 'internal-2'] });
    syncReq.flush({ archivedCount: 2, notFoundIds: [] });
  });

  it('marks a GraphQL error response as failed and continues the queue', () => {
    service.startDelete('set-1', 'sensitron', EMOTES);

    httpMock.expectOne(GQL_ENDPOINT).flush({ errors: [{ message: 'emote not found' }] });

    expect(service.queue()[0].status).toBe('failed');
    expect(service.queue()[0].errorMessage).toBe('emote not found');

    vi.advanceTimersByTime(DELETE_DELAY_MS);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(DELETE_DELAY_MS);

    // Only the successful one gets synced back to Postgres.
    const syncReq = httpMock.expectOne('/api/channels/sensitron/emotes/sync-deleted');
    expect(syncReq.request.body).toEqual({ emoteIds: ['internal-2'] });
    syncReq.flush({ archivedCount: 1, notFoundIds: [] });
  });

  it('clears the stored token and reports a friendly message on 401', () => {
    service.startDelete('set-1', 'sensitron', EMOTES);

    httpMock.expectOne(GQL_ENDPOINT).flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(tokenService.hasToken()).toBe(false);
    expect(service.queue()[0].status).toBe('failed');
    expect(service.queue()[0].errorMessage).toContain('Token ungültig');

    vi.advanceTimersByTime(DELETE_DELAY_MS);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(DELETE_DELAY_MS);
    httpMock.expectOne('/api/channels/sensitron/emotes/sync-deleted').flush({ archivedCount: 1, notFoundIds: [] });
  });

  it('cancel() marks pending/in-progress items as cancelled and stops the run', () => {
    service.startDelete('set-1', 'sensitron', EMOTES);
    httpMock.expectOne(GQL_ENDPOINT).flush({});

    // Second item is now 'pending', waiting out the inter-request delay — cancel before it fires.
    service.cancel();

    expect(service.isRunning()).toBe(false);
    expect(service.queue().map((i) => i.status)).toEqual(['done', 'cancelled']);

    // Only the already-done emote gets synced back.
    const syncReq = httpMock.expectOne('/api/channels/sensitron/emotes/sync-deleted');
    expect(syncReq.request.body).toEqual({ emoteIds: ['internal-1'] });
    syncReq.flush({ archivedCount: 1, notFoundIds: [] });

    // Advancing time afterwards must not fire the second (cancelled) request.
    vi.advanceTimersByTime(DELETE_DELAY_MS);
    httpMock.expectNone(GQL_ENDPOINT);
  });

  it('reset() clears the queue', () => {
    service.startDelete('set-1', 'sensitron', EMOTES);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    service.cancel();
    httpMock.expectOne('/api/channels/sensitron/emotes/sync-deleted').flush({ archivedCount: 1, notFoundIds: [] });

    service.reset();

    expect(service.queue()).toEqual([]);
  });

  it('does not start a second run while one is already in progress', () => {
    service.startDelete('set-1', 'sensitron', EMOTES);
    const firstQueueLength = service.queue().length;

    service.startDelete('set-2', 'other-channel', [EMOTES[0]]);

    expect(service.queue().length).toBe(firstQueueLength);

    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(DELETE_DELAY_MS);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(DELETE_DELAY_MS);
    httpMock.expectOne('/api/channels/sensitron/emotes/sync-deleted').flush({ archivedCount: 2, notFoundIds: [] });
  });
});
