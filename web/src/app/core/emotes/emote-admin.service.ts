import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';

export interface SyncDeletedResult {
  archivedCount: number;
  notFoundIds: string[];
}

export interface EmoteSetWarning {
  available: boolean;
  isOwnSet: boolean;
  otherTrackedChannelsSharingSet: string[];
  otherModeratedChannelsSharingSet: string[];
}

@Injectable({ providedIn: 'root' })
export class EmoteAdminService {
  private readonly http = inject(HttpClient);

  /** Reports already-deleted (7TV-side) internal emote ids so Postgres reflects it immediately —
   *  the 1-minute SevenTvPeriodicResyncWorker is the actual safety net regardless. */
  syncDeleted(channelName: string, emoteIds: string[]): Observable<SyncDeletedResult> {
    return this.http.post<SyncDeletedResult>(`/api/channels/${channelName}/emotes/sync-deleted`, { emoteIds });
  }

  /** Best-effort check whether this channel's active 7TV set is shared with/owned by someone else —
   *  see EmoteSetOwnershipService, can never be fully complete (7TV has no reverse "who else has this
   *  set active" lookup). */
  getSetWarning(channelName: string): Observable<EmoteSetWarning> {
    return this.http.get<EmoteSetWarning>(`/api/channels/${channelName}/emotes/set-warning`);
  }

  /** Deliberately separate from ChannelService.getStatus (management-only): a 7TV editor without
   *  Twitch-mod status must still see this to render the mass-delete panel's "Löschen" button. */
  getActiveEmoteSetId(channelName: string): Observable<{ activeEmoteSetId: string }> {
    return this.http.get<{ activeEmoteSetId: string }>(`/api/channels/${channelName}/emotes/active-set`);
  }
}
