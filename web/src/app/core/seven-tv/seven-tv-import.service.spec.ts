import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { ImportOrigin, ImportRow } from './import-source';
import { SevenTvImportService } from './seven-tv-import.service';
import { RUN_DELAY_MS } from './seven-tv-run-engine';
import { SevenTvTokenService } from './seven-tv-token.service';

// Only the keys the run engine itself translates — the import service adds none of its own.
const DE_TRANSLATIONS = {
  massDelete: {
    errors: {
      tokenInvalid: 'Token ungültig.',
      rateLimited: 'Rate Limit.',
      networkError: 'Netzwerkfehler.',
      genericStatus: '7TV-Fehler ({{ status }}).',
      rateLimitedGaveUp: 'Übersprungen.',
    },
  },
};

const GQL_ENDPOINT = 'https://7tv.io/v3/gql';
const TARGET_B = { setId: 'set-b', channelName: 'kanal_b' };
const TARGET_C = { setId: 'set-c', channelName: 'kanal_c' };
const SYNC_IMPORTED_B = '/api/channels/kanal_b/emotes/sync-imported';
const RESYNC_B = '/api/channels/kanal_b/resync';
const SYNC_IMPORTED_C = '/api/channels/kanal_c/emotes/sync-imported';
const RESYNC_C = '/api/channels/kanal_c/resync';

const CHANNEL_ORIGIN: ImportOrigin = { kind: 'channel', channelName: 'brudivoeller_tv' };
const FOREIGN_CHANNEL_ORIGIN: ImportOrigin = {
  kind: 'seventv-channel',
  channelName: 'handofblood',
};
const FILE_ORIGIN: ImportOrigin = {
  kind: 'file',
  fileName: 'emotepurge_brudivoeller_tv_emote-list_2026-09-05.json',
  exportedAt: '2026-09-05T10:00:00Z',
  // Deliberately set: the file *knows* a channel, and the body must still send null (R3).
  channelName: 'brudivoeller_tv',
  envelopeKind: 'emote-list',
};

const ROWS: ImportRow[] = [
  { sevenTvEmoteId: '7tv-1', name: 'PogU' },
  { sevenTvEmoteId: '7tv-2', name: 'KEKW' },
];

describe('SevenTvImportService', () => {
  let service: SevenTvImportService;
  let tokenService: SevenTvTokenService;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    sessionStorage.clear();
    vi.useFakeTimers();
    vi.spyOn(console, 'info').mockImplementation(() => undefined);
    TestBed.configureTestingModule({
      imports: [
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de', 'en'], defaultLang: 'de' },
        }),
      ],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));
    service = TestBed.inject(SevenTvImportService);
    tokenService = TestBed.inject(SevenTvTokenService);
    httpMock = TestBed.inject(HttpTestingController);
    tokenService.setToken('write-token');
  });

  afterEach(() => {
    httpMock.verify();
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  /** Runs both rows of ROWS to 'done' and drains the closing calls of a successful run. */
  function runTwoRowsToDone(): void {
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);
  }

  it('keys every queue row by its 7TV id and carries no internal emoteId', () => {
    service.startImport(TARGET_B, CHANNEL_ORIGIN, ROWS);

    expect(service.queue().map((item) => item.key)).toEqual(['7tv-1', '7tv-2']);
    expect(Object.hasOwn(service.queue()[0], 'emoteId')).toBe(false);

    const req = httpMock.expectOne(GQL_ENDPOINT);
    expect(req.request.headers.get('Authorization')).toBe('Bearer write-token');
    expect(req.request.body.query).toContain('action: ADD');
    expect(req.request.body.variables).toEqual({
      setId: 'set-b',
      emoteId: '7tv-1',
      name: 'PogU',
    });
    req.flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);
    httpMock.expectOne(SYNC_IMPORTED_B).flush(null, { status: 204, statusText: 'No Content' });
    httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
  });

  it('reports a channel import with its source channel and triggers exactly one resync', () => {
    service.startImport(TARGET_B, CHANNEL_ORIGIN, ROWS);
    runTwoRowsToDone();

    const reportReq = httpMock.expectOne(SYNC_IMPORTED_B);
    expect(reportReq.request.body).toEqual({
      sevenTvEmoteIds: ['7tv-1', '7tv-2'],
      sourceChannelName: 'brudivoeller_tv',
      sourceKind: 'channel',
    });
    expect(service.syncReport()).toBe('pending');
    reportReq.flush(null, { status: 204, statusText: 'No Content' });
    expect(service.syncReport()).toBe('succeeded');

    const resyncReq = httpMock.expectOne(RESYNC_B);
    expect(service.resyncTrigger()).toBe('pending');
    resyncReq.flush(null, { status: 202, statusText: 'Accepted' });

    expect(service.resyncTrigger()).toBe('succeeded');
    httpMock.expectNone(RESYNC_B);
    expect(service.run()?.result?.doneKeys).toEqual(['7tv-1', '7tv-2']);
  });

  it('reports a foreign-channel import with both its kind and its source channel', () => {
    // The expensive failure this pins (spec F6): the body used to be built with a
    // `kind === 'channel'` test, which sent `sourceChannelName: null` for this origin. The server
    // rejects a non-file kind without a name with a 400 — and this call runs *after* the ADD
    // mutations, so the emotes would already be copied and their origin lost for good.
    service.startImport(TARGET_B, FOREIGN_CHANNEL_ORIGIN, ROWS);
    runTwoRowsToDone();

    const reportReq = httpMock.expectOne(SYNC_IMPORTED_B);
    expect(reportReq.request.body).toEqual({
      sevenTvEmoteIds: ['7tv-1', '7tv-2'],
      sourceChannelName: 'handofblood',
      sourceKind: 'seventv-channel',
    });
    reportReq.flush(null, { status: 204, statusText: 'No Content' });
    expect(service.syncReport()).toBe('succeeded');

    httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
    expect(service.resyncTrigger()).toBe('succeeded');
  });

  it('sends sourceChannelName null for a file import even when the file names a channel', () => {
    service.startImport(TARGET_B, FILE_ORIGIN, ROWS);
    runTwoRowsToDone();

    const reportReq = httpMock.expectOne(SYNC_IMPORTED_B);
    expect(reportReq.request.body).toEqual({
      sevenTvEmoteIds: ['7tv-1', '7tv-2'],
      sourceChannelName: null,
      sourceKind: 'file',
    });
    reportReq.flush(null, { status: 204, statusText: 'No Content' });
    httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
  });

  it('aborts the whole run on a 7TV privileges rejection and reports nothing', () => {
    const threeRows = [...ROWS, { sevenTvEmoteId: '7tv-3', name: 'Sadge' }];
    service.startImport(TARGET_B, CHANNEL_ORIGIN, threeRows);

    httpMock
      .expectOne(GQL_ENDPOINT)
      .flush({ errors: [{ message: 'Insufficient Privileges for emote set' }] });

    expect(service.queue().map((item) => item.status)).toEqual([
      'failed',
      'cancelled',
      'cancelled',
    ]);
    expect(service.abortedForPrivileges()).toBe(true);
    expect(service.isRunning()).toBe(false);
    expect(service.run()?.result?.doneKeys).toEqual([]);
    expect(service.syncReport()).toBe('idle');
    httpMock.expectNone(SYNC_IMPORTED_B);
    httpMock.expectNone(RESYNC_B);
  });

  it('aborts on a 401 as well — the token is gone, every further row would fail the same way', () => {
    service.startImport(TARGET_B, CHANNEL_ORIGIN, ROWS);

    httpMock.expectOne(GQL_ENDPOINT).flush({}, { status: 401, statusText: 'Unauthorized' });

    expect(service.queue().map((item) => item.status)).toEqual(['failed', 'cancelled']);
    expect(service.abortedForPrivileges()).toBe(true);
    httpMock.expectNone(SYNC_IMPORTED_B);
  });

  it('keeps running on an ordinary 7TV rejection', () => {
    service.startImport(TARGET_B, CHANNEL_ORIGIN, ROWS);

    httpMock
      .expectOne(GQL_ENDPOINT)
      .flush({ errors: [{ message: 'BAD_REQUEST this emote has a conflicting name' }] });
    vi.advanceTimersByTime(RUN_DELAY_MS);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);

    expect(service.queue().map((item) => item.status)).toEqual(['failed', 'done']);
    expect(service.abortedForPrivileges()).toBe(false);

    const reportReq = httpMock.expectOne(SYNC_IMPORTED_B);
    expect(reportReq.request.body.sevenTvEmoteIds).toEqual(['7tv-2']);
    reportReq.flush(null, { status: 204, statusText: 'No Content' });
    httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
  });

  it('reports the resync cooldown as "coming on its own" and leaves the report untouched', () => {
    service.startImport(TARGET_B, CHANNEL_ORIGIN, ROWS);
    runTwoRowsToDone();

    httpMock.expectOne(SYNC_IMPORTED_B).flush(null, { status: 204, statusText: 'No Content' });
    httpMock
      .expectOne(RESYNC_B)
      .flush({ errorCode: 'resync_cooldown_active' }, { status: 429, statusText: 'Too Many' });

    expect(service.resyncTrigger()).toBe('cooldown');
    expect(service.syncReport()).toBe('succeeded');
  });

  it('re-sends a failed report against the current run on retrySyncReport()', () => {
    service.startImport(TARGET_B, CHANNEL_ORIGIN, ROWS);
    runTwoRowsToDone();

    // 401 is not retried automatically — waiting cannot fix an expired session.
    httpMock.expectOne(SYNC_IMPORTED_B).flush({}, { status: 401, statusText: 'Unauthorized' });
    expect(service.syncReport()).toBe('failed');
    httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });

    service.retrySyncReport();

    const retryReq = httpMock.expectOne(SYNC_IMPORTED_B);
    expect(retryReq.request.body.sevenTvEmoteIds).toEqual(['7tv-1', '7tv-2']);
    retryReq.flush(null, { status: 204, statusText: 'No Content' });
    expect(service.syncReport()).toBe('succeeded');
  });

  it('reset() clears queue, run, report and resync state', () => {
    service.startImport(TARGET_B, CHANNEL_ORIGIN, ROWS);
    runTwoRowsToDone();
    httpMock.expectOne(SYNC_IMPORTED_B).flush(null, { status: 204, statusText: 'No Content' });
    httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });

    service.reset();

    expect(service.queue()).toEqual([]);
    expect(service.run()).toBeNull();
    expect(service.syncReport()).toBe('idle');
    expect(service.resyncTrigger()).toBe('idle');
    expect(service.abortedForPrivileges()).toBe(false);
  });

  // R15: the engine sets isRunning false *before* the closing calls go out, so a second run can be
  // started while the first one's follow-up is still in flight. Everything the follow-up needs hangs
  // off the run record it closed over, and a late answer that no longer matches run() is dropped.
  it('drops the follow-up answers of a superseded run and retries the current one instead', () => {
    service.startImport(TARGET_B, CHANNEL_ORIGIN, ROWS);
    runTwoRowsToDone();

    const staleReport = httpMock.expectOne(SYNC_IMPORTED_B);
    const staleResync = httpMock.expectOne(RESYNC_B);
    expect(service.syncReport()).toBe('pending');

    // Run 2, started while run 1's follow-up is still open — the engine allows it, isRunning is
    // already false.
    expect(service.isRunning()).toBe(false);
    service.startImport(TARGET_C, FILE_ORIGIN, [{ sevenTvEmoteId: '7tv-9', name: 'Clueless' }]);
    expect(service.isRunning()).toBe(true);

    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);
    httpMock.expectOne(SYNC_IMPORTED_C).flush(null, { status: 204, statusText: 'No Content' });
    httpMock.expectOne(RESYNC_C).flush(null, { status: 202, statusText: 'Accepted' });
    expect(service.syncReport()).toBe('succeeded');
    expect(service.resyncTrigger()).toBe('succeeded');

    // Run 1 answers late, and badly — without the guard this would flip both signals of run 2.
    staleReport.flush({}, { status: 401, statusText: 'Unauthorized' });
    staleResync.flush({ errorCode: 'resync_cooldown_active' }, { status: 429, statusText: 'Too' });

    expect(service.syncReport()).toBe('succeeded');
    expect(service.resyncTrigger()).toBe('succeeded');
    expect(service.run()?.targetChannelName).toBe('kanal_c');
    expect(service.run()?.result?.doneKeys).toEqual(['7tv-9']);

    // And a retry now belongs to run 2: run 2's keys, run 2's channel — never run 1's.
    service.retrySyncReport();
    const retryReq = httpMock.expectOne(SYNC_IMPORTED_C);
    expect(retryReq.request.body).toEqual({
      sevenTvEmoteIds: ['7tv-9'],
      sourceChannelName: null,
      sourceKind: 'file',
    });
    retryReq.flush(null, { status: 204, statusText: 'No Content' });
    httpMock.expectNone(SYNC_IMPORTED_B);
  });
});
