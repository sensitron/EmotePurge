import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { AdminChannel, AdminHealth, AuditLogEntry } from './admin.model';
import { AdminService } from './admin.service';
import { PagedResult } from '../models/paged-result.model';

describe('AdminService', () => {
  let service: AdminService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(AdminService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('getHealth GETs /api/admin/health', () => {
    const health: AdminHealth = {
      snapshotAvailable: true,
      status: 'connected',
      isConnected: true,
      lastMessageReceivedUtc: '2026-07-31T12:00:00Z',
      connectAttemptedUtc: '2026-07-31T11:00:00Z',
      secondsSinceLastMessage: 12,
      sevenTv: {
        status: 'connected',
        enabled: true,
        connected: true,
        lastFrameUtc: '2026-07-31T12:00:00Z',
        lastDispatchUtc: '2026-07-31T11:59:00Z',
        connectAttemptedUtc: '2026-07-31T11:00:00Z',
        secondsSinceLastFrame: 5,
        desiredChannelCount: 7,
        desiredSubscriptionCount: 14,
        unacknowledgedCount: 0,
        subscriptionLimit: 500,
      },
      flush: {
        consecutiveFailures: 0,
        lastSuccessUtc: '2026-07-31T11:59:30Z',
        lastRowCount: 42,
        pendingEmoteCount: 3,
      },
    };

    let result: AdminHealth | undefined;
    service.getHealth().subscribe((r) => (result = r));

    const req = httpMock.expectOne('/api/admin/health');
    expect(req.request.method).toBe('GET');
    req.flush(health);

    expect(result).toEqual(health);
  });

  it('passes through a snapshot-less response (worker gone: every detail null)', () => {
    // The Api answers with the same shape rather than a shorter error body, so the page renders one
    // layout — this pins that the service does not massage it into something else.
    let result: AdminHealth | undefined;
    service.getHealth().subscribe((r) => (result = r));

    httpMock.expectOne('/api/admin/health').flush({
      snapshotAvailable: false,
      status: 'unknown',
      isConnected: false,
      lastMessageReceivedUtc: null,
      connectAttemptedUtc: null,
      secondsSinceLastMessage: null,
      sevenTv: {
        status: 'unknown',
        enabled: false,
        connected: false,
        lastFrameUtc: null,
        lastDispatchUtc: null,
        connectAttemptedUtc: null,
        secondsSinceLastFrame: null,
        desiredChannelCount: null,
        desiredSubscriptionCount: null,
        unacknowledgedCount: null,
        subscriptionLimit: 500,
      },
      flush: {
        consecutiveFailures: null,
        lastSuccessUtc: null,
        lastRowCount: null,
        pendingEmoteCount: null,
      },
    });

    expect(result?.snapshotAvailable).toBe(false);
    expect(result?.status).toBe('unknown');
    expect(result?.sevenTv.subscriptionLimit).toBe(500);
  });

  it('listChannels GETs /api/admin/channels', () => {
    const channels: AdminChannel[] = [
      {
        channelName: 'handofblood',
        twitchChannelId: '4711',
        isBotActive: true,
        createdAt: '2026-01-01T00:00:00Z',
        emoteCount: 903,
        archivedEmoteCount: 17,
        activeVoteSessionCount: 1,
        voteSessionCount: 4,
        lastSyncedAtUtc: '2026-07-31T11:00:00Z',
      },
    ];

    let result: AdminChannel[] | undefined;
    service.listChannels().subscribe((r) => (result = r));

    const req = httpMock.expectOne('/api/admin/channels');
    expect(req.request.method).toBe('GET');
    req.flush(channels);

    expect(result).toEqual(channels);
  });

  it('listChannels passes a never-synced channel through unchanged (nulls stay nulls)', () => {
    // A freshly joined channel has no emote rows, so lastSyncedAtUtc is null — the service must not
    // coerce that into a date, because "never synced" must stay distinguishable in the UI.
    let result: AdminChannel[] | undefined;
    service.listChannels().subscribe((r) => (result = r));

    httpMock.expectOne('/api/admin/channels').flush([
      {
        channelName: 'freshchannel',
        twitchChannelId: null,
        isBotActive: false,
        createdAt: '2026-07-31T10:00:00Z',
        emoteCount: 0,
        archivedEmoteCount: 0,
        activeVoteSessionCount: 0,
        voteSessionCount: 0,
        lastSyncedAtUtc: null,
      },
    ]);

    expect(result?.[0].lastSyncedAtUtc).toBeNull();
    expect(result?.[0].twitchChannelId).toBeNull();
  });

  it('listAuditLog GETs /api/admin/audit-log with paging params', () => {
    const entries: AuditLogEntry[] = [
      {
        id: 2,
        occurredAtUtc: '2026-07-31T12:00:00Z',
        actorTwitchUserId: '1',
        actorLogin: 'sensitron',
        action: 'channel.purge',
        channelName: 'handofblood',
        targetType: null,
        targetId: null,
        detailsJson: null,
      },
    ];
    const page: PagedResult<AuditLogEntry> = {
      items: entries,
      page: 2,
      pageSize: 25,
      totalCount: 30,
      totalPages: 2,
    };

    let result: PagedResult<AuditLogEntry> | undefined;
    service.listAuditLog(2, 25).subscribe((r) => (result = r));

    const req = httpMock.expectOne('/api/admin/audit-log?page=2&pageSize=25');
    expect(req.request.method).toBe('GET');
    req.flush(page);

    expect(result).toEqual(page);
  });

  it('listAuditLog defaults to the first page with pageSize 25', () => {
    service.listAuditLog().subscribe();

    httpMock.expectOne('/api/admin/audit-log?page=1&pageSize=25').flush({
      items: [],
      page: 1,
      pageSize: 25,
      totalCount: 0,
      totalPages: 0,
    });
  });

  it('listAuditLog appends only the filter params that are set', () => {
    service.listAuditLog(1, 25, { action: 'channel.purge', channel: 'handofblood' }).subscribe();

    // No `actor` param: an unset filter field must not appear as an empty query param, otherwise
    // the backend would treat "" as a filter value and the URL stops being cache-comparable.
    httpMock
      .expectOne('/api/admin/audit-log?page=1&pageSize=25&action=channel.purge&channel=handofblood')
      .flush({ items: [], page: 1, pageSize: 25, totalCount: 0, totalPages: 0 });
  });

  it('listAuditLog trims text filters and drops blank ones', () => {
    service.listAuditLog(1, 25, { channel: '  HandOfBlood  ', actor: '   ' }).subscribe();

    // Trimming here keeps the URL stable; case-normalization stays server-side next to Regel 9.
    httpMock
      .expectOne('/api/admin/audit-log?page=1&pageSize=25&channel=HandOfBlood')
      .flush({ items: [], page: 1, pageSize: 25, totalCount: 0, totalPages: 0 });
  });

  it('hands detailsJson through as raw text, unparsed', () => {
    // The column is free-form per action; parsing belongs in the page (defensively), not in the
    // service — a service that parsed it would have to decide what an unknown shape means.
    let result: PagedResult<AuditLogEntry> | undefined;
    service.listAuditLog(1, 25).subscribe((r) => (result = r));

    httpMock.expectOne('/api/admin/audit-log?page=1&pageSize=25').flush({
      items: [
        {
          id: 1,
          occurredAtUtc: '2026-07-31T11:00:00Z',
          actorTwitchUserId: '1',
          actorLogin: 'sensitron',
          action: 'emotes.syncDeleted',
          channelName: 'handofblood',
          targetType: null,
          targetId: null,
          detailsJson: '{"emoteCount": 12}',
        },
      ],
      page: 1,
      pageSize: 25,
      totalCount: 1,
      totalPages: 1,
    });

    expect(result?.items[0].detailsJson).toBe('{"emoteCount": 12}');
  });
});
