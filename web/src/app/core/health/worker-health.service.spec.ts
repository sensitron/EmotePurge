import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { WorkerHealthService } from './worker-health.service';

describe('WorkerHealthService', () => {
  let httpMock: HttpTestingController;

  beforeEach(() => {
    vi.useFakeTimers();
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
    vi.useRealTimers();
  });

  it('starts at "unknown" and polls /api/worker/health immediately', () => {
    const service = TestBed.inject(WorkerHealthService);

    expect(service.status()).toBe('unknown');

    const req = httpMock.expectOne('/api/worker/health');
    expect(req.request.method).toBe('GET');
    req.flush({ status: 'connected' });

    expect(service.status()).toBe('connected');
  });

  it('maps "disconnected" to the "stale" status', () => {
    const service = TestBed.inject(WorkerHealthService);

    httpMock.expectOne('/api/worker/health').flush({ status: 'disconnected' });

    expect(service.status()).toBe('stale');
  });

  it('falls back to "unknown" when the request errors', () => {
    const service = TestBed.inject(WorkerHealthService);

    httpMock
      .expectOne('/api/worker/health')
      .flush(null, { status: 500, statusText: 'Server Error' });

    expect(service.status()).toBe('unknown');
  });

  it('polls again every 30 seconds', () => {
    const service = TestBed.inject(WorkerHealthService);

    httpMock.expectOne('/api/worker/health').flush({ status: 'connected' });
    expect(service.status()).toBe('connected');

    vi.advanceTimersByTime(30_000);
    httpMock.expectOne('/api/worker/health').flush({ status: 'disconnected' });
    expect(service.status()).toBe('stale');

    vi.advanceTimersByTime(30_000);
    httpMock.expectOne('/api/worker/health').flush({ status: 'connected' });
    expect(service.status()).toBe('connected');
  });
});
