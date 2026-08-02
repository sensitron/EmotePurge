import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';

import { ChannelPermissions, ChannelStatus, MyChannelsResult } from './channel.model';

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

  /**
   * Asks the worker for a full 7TV resync. Answers 202 — the command protocol is one-way, so this
   * only means "the worker was told". Completion arrives as a `channel.synced` live event.
   *
   * Available to everyone who may see the usage stats, including the channel's 7TV editors: they
   * are usually the ones who just added the emote that is not showing up yet. Guarded server-side
   * by a per-channel cooldown, which answers 429 with `resync_cooldown_active`.
   */
  resync(channelName: string): Observable<void> {
    return this.http.post<void>(`/api/channels/${channelName}/resync`, {});
  }

  /**
   * Irreversible: removes the channel with its emotes, usage history, vote sessions and votes
   * (server-side cascades). Global-admin-only — deliberately not reachable from any channel-scoped
   * screen, only from the admin channel page, and only behind a typed name confirmation.
   */
  purge(channelName: string): Observable<void> {
    return this.http.delete<void>(`/api/channels/${channelName}/purge`);
  }

  listMine(): Observable<MyChannelsResult> {
    return this.http.get<MyChannelsResult>('/api/channels/mine');
  }
}
