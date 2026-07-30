import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';

import {
  AdminChannelDto,
  ChannelPermissions,
  ChannelStatus,
  MyChannelsResult,
} from './channel.model';

@Injectable({ providedIn: 'root' })
export class ChannelService {
  private readonly http = inject(HttpClient);

  getStatus(channelName: string): Observable<ChannelStatus> {
    return this.http.get<ChannelStatus>(`/api/channels/${channelName}`);
  }

  /**
   * The one permission read for a channel. Succeeds for every logged-in user (it reports what they
   * may do, it does not gate on it), so callers read the flags — they must not treat a 403 as
   * "not allowed", the way the probe calls this replaced had to.
   */
  getPermissions(channelName: string): Observable<ChannelPermissions> {
    return this.http.get<ChannelPermissions>(`/api/channels/${channelName}/permissions`);
  }

  join(channelName: string): Observable<ChannelStatus> {
    return this.http.post<ChannelStatus>(`/api/channels/${channelName}/join`, {});
  }

  leave(channelName: string): Observable<void> {
    return this.http.delete<void>(`/api/channels/${channelName}`);
  }

  /** Admin-only — 403s for everyone else, which callers treat as "hide the admin section". */
  listAll(): Observable<AdminChannelDto[]> {
    return this.http.get<AdminChannelDto[]>('/api/channels');
  }

  listMine(): Observable<MyChannelsResult> {
    return this.http.get<MyChannelsResult>('/api/channels/mine');
  }
}
