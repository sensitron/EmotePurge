import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';

import { ChannelStatus } from './channel.model';

@Injectable({ providedIn: 'root' })
export class ChannelService {
  private readonly http = inject(HttpClient);

  getStatus(channelName: string): Observable<ChannelStatus> {
    return this.http.get<ChannelStatus>(`/api/channels/${channelName}`);
  }

  join(channelName: string): Observable<ChannelStatus> {
    return this.http.post<ChannelStatus>(`/api/channels/${channelName}/join`, {});
  }

  leave(channelName: string): Observable<void> {
    return this.http.delete<void>(`/api/channels/${channelName}`);
  }
}
