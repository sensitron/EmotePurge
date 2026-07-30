import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { AdminChannelDto, ChannelPermissions, ChannelStatus, MyChannelsResult } from './channel.model';
import { ChannelService } from './channel.service';

describe('ChannelService', () => {
  let service: ChannelService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(ChannelService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('getStatus GETs /api/channels/{channelName}', () => {
    const status: ChannelStatus = {
      channelId: '1',
      channelName: 'sensitron',
      isBotActive: true,
      activeEmoteSetId: 'set1',
    };

    let result: ChannelStatus | undefined;
    service.getStatus('sensitron').subscribe((r) => (result = r));

    const req = httpMock.expectOne('/api/channels/sensitron');
    expect(req.request.method).toBe('GET');
    req.flush(status);

    expect(result).toEqual(status);
  });

  it('getPermissions GETs /api/channels/{channelName}/permissions', () => {
    const permissions: ChannelPermissions = {
      canManage: false,
      canViewUsageStats: true,
      isGlobalAdmin: false,
      isTracked: true,
      isBotActive: false,
    };

    let result: ChannelPermissions | undefined;
    service.getPermissions('sensitron').subscribe((r) => (result = r));

    const req = httpMock.expectOne('/api/channels/sensitron/permissions');
    expect(req.request.method).toBe('GET');
    req.flush(permissions);

    expect(result).toEqual(permissions);
  });

  it('join POSTs to /api/channels/{channelName}/join with an empty body', () => {
    service.join('sensitron').subscribe();

    const req = httpMock.expectOne('/api/channels/sensitron/join');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({});
    req.flush({});
  });

  it('leave DELETEs /api/channels/{channelName}', () => {
    service.leave('sensitron').subscribe();

    const req = httpMock.expectOne('/api/channels/sensitron');
    expect(req.request.method).toBe('DELETE');
    req.flush(null);
  });

  it('listAll GETs /api/channels', () => {
    const dtos: AdminChannelDto[] = [];
    service.listAll().subscribe();

    const req = httpMock.expectOne('/api/channels');
    expect(req.request.method).toBe('GET');
    req.flush(dtos);
  });

  it('listMine GETs /api/channels/mine', () => {
    const result: MyChannelsResult = { helixUnavailable: false, sevenTvUnavailable: false, channels: [] };
    service.listMine().subscribe();

    const req = httpMock.expectOne('/api/channels/mine');
    expect(req.request.method).toBe('GET');
    req.flush(result);
  });
});
