import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';

export interface SyncDeletedResult {
  archivedCount: number;
  notFoundIds: string[];
}

@Injectable({ providedIn: 'root' })
export class EmoteAdminService {
  private readonly http = inject(HttpClient);

  /** Reports already-deleted (7TV-side) internal emote ids so Postgres reflects it immediately —
   *  the 1-minute SevenTvPeriodicResyncWorker is the actual safety net regardless. */
  syncDeleted(channelName: string, emoteIds: string[]): Observable<SyncDeletedResult> {
    return this.http.post<SyncDeletedResult>(`/api/channels/${channelName}/emotes/sync-deleted`, { emoteIds });
  }
}
