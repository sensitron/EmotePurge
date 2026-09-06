import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { EmoteAdminService } from './emote-admin.service';
import { ImportTargetLoadState, loadImportTarget } from './import-target-loader';

const STATUS_URL = '/api/channels/sensitron/emotes/active-set';
const EMOTES_URL = '/api/channels/sensitron/emotes';
const WARNING_URL = '/api/channels/sensitron/emotes/set-warning';

const READY_STATUS = {
  activeEmoteSetId: 'set-1',
  capacity: 1000,
  occupiedSlots: 847,
  trackedSince: '2026-06-12T09:14:00Z',
  syncFailureReason: null,
  lastSyncAttemptAtUtc: '2026-08-29T12:00:00Z',
  botsExcludedSince: null,
};

const READY_WARNING = {
  available: true,
  isOwnSet: true,
  otherTrackedChannelsSharingSet: [],
  otherModeratedChannelsSharingSet: [],
};

describe('loadImportTarget', () => {
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

  it('emits ready with all fields once all three requests succeed, never synchronously', () => {
    let result: ImportTargetLoadState | undefined;
    loadImportTarget(service, 'sensitron').subscribe((value) => (result = value));

    // The dialog opens on `loading` and fills in later — the loader must not resolve before any
    // response has come back, so it must not be observable synchronously right after subscribe.
    expect(result).toBeUndefined();

    httpMock.expectOne(STATUS_URL).flush(READY_STATUS);
    httpMock.expectOne(EMOTES_URL).flush({
      emotes: [{ sevenTvEmoteId: '7tv-1', name: 'PogU' }],
    });
    httpMock.expectOne(WARNING_URL).flush(READY_WARNING);

    expect(result).toEqual({
      status: 'ready',
      setId: 'set-1',
      occupiedSlots: 847,
      capacity: 1000,
      syncFailureReason: null,
      emotes: [{ sevenTvEmoteId: '7tv-1', name: 'PogU' }],
      warning: READY_WARNING,
    });
  });

  it('emits ready with an unavailable warning when only getSetWarning fails', () => {
    let result: ImportTargetLoadState | undefined;
    loadImportTarget(service, 'sensitron').subscribe((value) => (result = value));

    httpMock.expectOne(STATUS_URL).flush(READY_STATUS);
    httpMock.expectOne(EMOTES_URL).flush({ emotes: [] });
    httpMock.expectOne(WARNING_URL).flush('boom', { status: 500, statusText: 'Server Error' });

    expect(result).toEqual({
      status: 'ready',
      setId: 'set-1',
      occupiedSlots: 847,
      capacity: 1000,
      syncFailureReason: null,
      emotes: [],
      warning: {
        available: false,
        isOwnSet: false,
        otherTrackedChannelsSharingSet: [],
        otherModeratedChannelsSharingSet: [],
      },
    });
  });

  it('emits no-set when getSetStatus 404s', () => {
    let result: ImportTargetLoadState | undefined;
    loadImportTarget(service, 'sensitron').subscribe((value) => (result = value));

    httpMock.expectOne(STATUS_URL).flush('not found', { status: 404, statusText: 'Not Found' });
    httpMock.expectOne(EMOTES_URL).flush({ emotes: [] });
    httpMock.expectOne(WARNING_URL).flush(READY_WARNING);

    expect(result).toEqual({ status: 'no-set' });
  });

  it('emits failed when listEmotes fails with a non-404 status', () => {
    let result: ImportTargetLoadState | undefined;
    loadImportTarget(service, 'sensitron').subscribe((value) => (result = value));

    httpMock.expectOne(STATUS_URL).flush(READY_STATUS);
    httpMock.expectOne(EMOTES_URL).flush('boom', { status: 500, statusText: 'Server Error' });
    httpMock.expectOne(WARNING_URL).flush(READY_WARNING);

    expect(result).toEqual({ status: 'failed' });
  });

  it('emits no-set when getSetStatus succeeds with an empty active set id', () => {
    let result: ImportTargetLoadState | undefined;
    loadImportTarget(service, 'sensitron').subscribe((value) => (result = value));

    httpMock.expectOne(STATUS_URL).flush({
      activeEmoteSetId: '',
      capacity: null,
      occupiedSlots: 0,
      trackedSince: '2026-06-12T09:14:00Z',
      syncFailureReason: null,
      lastSyncAttemptAtUtc: null,
      botsExcludedSince: null,
    });
    httpMock.expectOne(EMOTES_URL).flush({ emotes: [] });
    httpMock.expectOne(WARNING_URL).flush(READY_WARNING);

    expect(result).toEqual({ status: 'no-set' });
  });

  it('prefers no-set over failed when listEmotes 404s and getSetStatus fails with a 500', () => {
    let result: ImportTargetLoadState | undefined;
    loadImportTarget(service, 'sensitron').subscribe((value) => (result = value));

    httpMock.expectOne(STATUS_URL).flush('boom', { status: 500, statusText: 'Server Error' });
    httpMock.expectOne(EMOTES_URL).flush('not found', { status: 404, statusText: 'Not Found' });
    httpMock.expectOne(WARNING_URL).flush(READY_WARNING);

    expect(result).toEqual({ status: 'no-set' });
  });

  it('emits a single failed value and completes without erroring when all three requests fail', () => {
    let nextCount = 0;
    let result: ImportTargetLoadState | undefined;
    let completed = false;
    let erroredValue: unknown;

    loadImportTarget(service, 'sensitron').subscribe({
      next: (value) => {
        nextCount++;
        result = value;
      },
      error: (error) => {
        erroredValue = error;
      },
      complete: () => {
        completed = true;
      },
    });

    httpMock.expectOne(STATUS_URL).flush('boom', { status: 500, statusText: 'Server Error' });
    httpMock.expectOne(EMOTES_URL).flush('boom', { status: 500, statusText: 'Server Error' });
    httpMock.expectOne(WARNING_URL).flush('boom', { status: 500, statusText: 'Server Error' });

    expect(nextCount).toBe(1);
    expect(result).toEqual({ status: 'failed' });
    expect(completed).toBe(true);
    expect(erroredValue).toBeUndefined();
  });

  it('supports a retry by calling it again, with no state to clear first', () => {
    const results: ImportTargetLoadState[] = [];

    loadImportTarget(service, 'sensitron').subscribe((value) => results.push(value));
    httpMock.expectOne(STATUS_URL).flush('boom', { status: 500, statusText: 'Server Error' });
    httpMock.expectOne(EMOTES_URL).flush({ emotes: [] });
    httpMock.expectOne(WARNING_URL).flush(READY_WARNING);

    expect(results).toEqual([{ status: 'failed' }]);

    loadImportTarget(service, 'sensitron').subscribe((value) => results.push(value));
    httpMock.expectOne(STATUS_URL).flush(READY_STATUS);
    httpMock.expectOne(EMOTES_URL).flush({ emotes: [] });
    httpMock.expectOne(WARNING_URL).flush(READY_WARNING);

    expect(results[1]).toEqual({
      status: 'ready',
      setId: 'set-1',
      occupiedSlots: 847,
      capacity: 1000,
      syncFailureReason: null,
      emotes: [],
      warning: READY_WARNING,
    });
  });
});
