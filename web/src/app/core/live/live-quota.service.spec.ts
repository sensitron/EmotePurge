import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { WritableSignal, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { LiveQuotaService } from './live-quota.service';
import { LiveStatus, LiveUpdateService } from './live-update.service';

describe('LiveQuotaService', () => {
  let service: LiveQuotaService;
  let httpMock: HttpTestingController;
  let fatalCloseCount: WritableSignal<number>;
  let status: WritableSignal<LiveStatus>;

  beforeEach(() => {
    fatalCloseCount = signal(0);
    status = signal<LiveStatus>('idle');

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          // Only the two signals this service reads — driving the real one would mean driving an
          // EventSource fake through a fatal close just to move a counter.
          provide: LiveUpdateService,
          useValue: { fatalCloseCount, status } as unknown as LiveUpdateService,
        },
      ],
    });
    httpMock = TestBed.inject(HttpTestingController);
    service = TestBed.inject(LiveQuotaService);
    // Flushes the constructor's effects, which see a zero close count and do nothing.
    TestBed.tick();
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('asks nothing while no stream has been refused', () => {
    // The whole point of probing only after a failure: a request on every page load would spend a
    // permit to learn a number that is only interesting once something has already gone wrong.
    expect(service.perSubscriberLimitReached()).toBe(false);
    httpMock.expectNone('/api/live/status');
  });

  it('reports the limit as reached when the server says this login is at its ceiling', () => {
    fatalCloseCount.set(1);
    TestBed.tick();

    const req = httpMock.expectOne('/api/live/status');
    expect(req.request.method).toBe('GET');
    req.flush({ openConnections: 6, maxPerSubscriber: 6, perSubscriberLimitReached: true });

    expect(service.perSubscriberLimitReached()).toBe(true);
  });

  it('stays quiet when the budget still had room — that refusal was not the user to blame', () => {
    // The process-wide ceiling and a Redis outage both land here. Neither is anything the person in
    // front of the screen can do something about, so telling them to close tabs would be wrong.
    fatalCloseCount.set(1);
    TestBed.tick();

    httpMock
      .expectOne('/api/live/status')
      .flush({ openConnections: 1, maxPerSubscriber: 6, perSubscriberLimitReached: false });

    expect(service.perSubscriberLimitReached()).toBe(false);
  });

  it('shows nothing when the probe itself fails', () => {
    fatalCloseCount.set(1);
    TestBed.tick();

    httpMock
      .expectOne('/api/live/status')
      .flush(null, { status: 503, statusText: 'Service Unavailable' });

    expect(service.perSubscriberLimitReached()).toBe(false);
  });

  it('clears the hint as soon as a stream opens again', () => {
    // The budget frees itself when a tab closes, so the hint has to disappear on its own — a stream
    // reaching 'open' is proof there was room for it.
    fatalCloseCount.set(1);
    TestBed.tick();
    httpMock
      .expectOne('/api/live/status')
      .flush({ openConnections: 6, maxPerSubscriber: 6, perSubscriberLimitReached: true });
    expect(service.perSubscriberLimitReached()).toBe(true);

    status.set('open');
    TestBed.tick();

    expect(service.perSubscriberLimitReached()).toBe(false);
  });

  it('drops a probe answer that arrives after the stream is back', () => {
    // The visibility retry can reopen a stream while the probe explaining the previous close is
    // still in flight. Without the generation check the late "your budget was full" lands after the
    // clear and sticks forever — status stays 'open', so nothing clears it again, and the header
    // warns about live updates on a page whose live updates work. Found by the Codex review.
    fatalCloseCount.set(1);
    TestBed.tick();
    const inFlight = httpMock.expectOne('/api/live/status');

    status.set('open');
    TestBed.tick();

    inFlight.flush({ openConnections: 6, maxPerSubscriber: 6, perSubscriberLimitReached: true });

    expect(service.perSubscriberLimitReached()).toBe(false);
  });

  it('asks again on a second refusal', () => {
    // A counter rather than a boolean on LiveUpdateService exists for exactly this: the second
    // failure after the first was cleared is still an event worth reacting to.
    fatalCloseCount.set(1);
    TestBed.tick();
    httpMock
      .expectOne('/api/live/status')
      .flush({ openConnections: 1, maxPerSubscriber: 6, perSubscriberLimitReached: false });

    fatalCloseCount.set(2);
    TestBed.tick();
    httpMock
      .expectOne('/api/live/status')
      .flush({ openConnections: 6, maxPerSubscriber: 6, perSubscriberLimitReached: true });

    expect(service.perSubscriberLimitReached()).toBe(true);
  });
});
