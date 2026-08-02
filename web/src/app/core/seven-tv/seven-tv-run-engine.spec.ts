import { HttpClient } from '@angular/common/http';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  RUN_DELAY_MS,
  RunOperation,
  RunQueueEmote,
  RunResult,
  SevenTvRunEngine,
} from './seven-tv-run-engine';
import { SevenTvTokenService } from './seven-tv-token.service';

const DE_TRANSLATIONS = {
  massDelete: {
    errors: {
      tokenInvalid: 'Token ungültig oder abgelaufen — bitte neues 7TV-Token eintragen.',
      rateLimited: 'Zu viele Anfragen an 7TV (Rate Limit) — später erneut versuchen.',
      networkError: 'Keine Verbindung zu 7TV möglich (Netzwerkfehler).',
      genericStatus: '7TV-Fehler (Status {{ status }}).',
      rateLimitedGaveUp:
        '7TV-Rate-Limit auch nach mehreren Wartezyklen aktiv — Emote übersprungen.',
    },
  },
};

const GQL_ENDPOINT = 'https://7tv.io/v3/gql';

const TEST_OPERATION: RunOperation = {
  label: 'test run',
  buildRequest: (setId, emote) => ({
    query: 'mutation Test',
    variables: { setId, emoteId: emote.sevenTvEmoteId },
  }),
};

const EMOTES: RunQueueEmote[] = [
  { emoteId: 'internal-1', sevenTvEmoteId: '7tv-1', name: 'PogU' },
  { emoteId: 'internal-2', sevenTvEmoteId: '7tv-2', name: 'KEKW' },
];

function rateLimitResponse(resetSeconds: number) {
  return {
    errors: [
      {
        message: 'RATE_LIMIT_EXCEEDED rate limit exceeded',
        extensions: {
          code: 'RATE_LIMIT_EXCEEDED',
          status: 429,
          headers: {
            'x-ratelimit-emote_set_change-remaining': '0',
            'x-ratelimit-emote_set_change-reset': String(resetSeconds),
            'x-ratelimit-emote_set_change-limit': '100',
          },
        },
      },
    ],
  };
}

describe('SevenTvRunEngine', () => {
  let engine: SevenTvRunEngine;
  let tokenService: SevenTvTokenService;
  let httpMock: HttpTestingController;
  let results: RunResult[];

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
    tokenService = TestBed.inject(SevenTvTokenService);
    engine = new SevenTvRunEngine(
      TestBed.inject(HttpClient),
      tokenService,
      TestBed.inject(TranslocoService),
    );
    httpMock = TestBed.inject(HttpTestingController);
    tokenService.setToken('write-token');
    results = [];
  });

  afterEach(() => {
    httpMock.verify();
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  function start(emotes: RunQueueEmote[] = EMOTES): boolean {
    return engine.start('set-1', emotes, TEST_OPERATION, (result) => results.push(result));
  }

  it('refuses to start without a token and reports it to the caller', () => {
    tokenService.clearToken();
    expect(start()).toBe(false);
    expect(engine.isRunning()).toBe(false);
    expect(engine.queue()).toEqual([]);
  });

  it('refuses to start on an empty list', () => {
    expect(start([])).toBe(false);
  });

  it('runs the queue sequentially through the operation and completes with the done ids', () => {
    expect(start()).toBe(true);
    expect(engine.queue().map((item) => item.status)).toEqual(['in-progress', 'pending']);

    const req1 = httpMock.expectOne(GQL_ENDPOINT);
    expect(req1.request.headers.get('Authorization')).toBe('Bearer write-token');
    expect(req1.request.body).toEqual({
      query: 'mutation Test',
      variables: { setId: 'set-1', emoteId: '7tv-1' },
    });
    req1.flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);

    httpMock.expectOne(GQL_ENDPOINT).flush({ errors: [{ message: 'boom' }] });
    vi.advanceTimersByTime(RUN_DELAY_MS);

    expect(engine.isRunning()).toBe(false);
    expect(results).toHaveLength(1);
    expect(results[0].doneIds).toEqual(['internal-1']);
    expect(results[0].items.map((item) => item.status)).toEqual(['done', 'failed']);
    expect(results[0].finishedAt).toBeGreaterThanOrEqual(results[0].startedAt);
  });

  it('waits out a GQL rate limit and retries instead of failing the emote', () => {
    start([EMOTES[0]]);
    httpMock.expectOne(GQL_ENDPOINT).flush(rateLimitResponse(30));

    expect(engine.queue()[0].status).toBe('in-progress');
    expect(engine.rateLimitPauseSeconds()).toBe(31);

    vi.advanceTimersByTime(30_500);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    // Generously past the re-paced inter-request delay (~330ms after the 100-per-30s answer).
    vi.advanceTimersByTime(1000);

    expect(engine.queue()[0].status).toBe('done');
    expect(results[0].doneIds).toEqual(['internal-1']);
  });

  it('backs off a bare HTTP 429 for a full window', () => {
    start([EMOTES[0]]);
    httpMock.expectOne(GQL_ENDPOINT).flush(null, { status: 429, statusText: 'Too Many Requests' });

    expect(engine.rateLimitPauseSeconds()).toBe(60);
    vi.advanceTimersByTime(60_000);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);

    expect(engine.queue()[0].status).toBe('done');
  });

  it('clears the token on 401 so the UI falls back to the prompt', () => {
    start([EMOTES[0]]);
    httpMock.expectOne(GQL_ENDPOINT).flush(null, { status: 401, statusText: 'Unauthorized' });
    vi.advanceTimersByTime(RUN_DELAY_MS);

    expect(tokenService.hasToken()).toBe(false);
    expect(engine.queue()[0].status).toBe('failed');
    expect(engine.queue()[0].errorMessage).toContain('Token ungültig');
  });

  it('cancel() marks the rest cancelled, keeps terminal states and still completes the run', () => {
    start();
    httpMock.expectOne(GQL_ENDPOINT).flush({});

    engine.cancel();

    expect(engine.isRunning()).toBe(false);
    expect(engine.queue().map((item) => item.status)).toEqual(['done', 'cancelled']);
    expect(results).toHaveLength(1);
    expect(results[0].doneIds).toEqual(['internal-1']);

    vi.advanceTimersByTime(RUN_DELAY_MS);
    httpMock.expectNone(GQL_ENDPOINT);
  });

  it('cancel() outside a run does nothing', () => {
    engine.cancel();
    expect(results).toEqual([]);
  });

  it('logs the closing measurement under the operation label', () => {
    start([EMOTES[0]]);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);

    expect(console.info).toHaveBeenCalledWith(
      '[EmotePurge] 7TV test run finished',
      expect.objectContaining({ requested: 1, succeeded: 1 }),
    );
  });
});
