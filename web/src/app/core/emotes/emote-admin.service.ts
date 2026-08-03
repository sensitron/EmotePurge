import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';

import { DuplicateEmoteName } from './duplicate-emote-name.model';
import { EmoteSetStatus } from './emote-set-status.model';

export interface SyncDeletedResult {
  archivedCount: number;
  notFoundIds: string[];
}

export interface SyncRestoredResult {
  restoredCount: number;
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
    return this.http.post<SyncDeletedResult>(`/api/channels/${channelName}/emotes/sync-deleted`, {
      emoteIds,
    });
  }

  /** The restore counterpart of syncDeleted: un-archives the re-added emotes server-side and —
   *  its actual purpose — writes the emotes.syncRestored audit entry. Without it a restore only
   *  ever appeared in the log as an anonymous channel.resync. */
  syncRestored(channelName: string, emoteIds: string[]): Observable<SyncRestoredResult> {
    return this.http.post<SyncRestoredResult>(`/api/channels/${channelName}/emotes/sync-restored`, {
      emoteIds,
    });
  }

  /** Best-effort check whether this channel's active 7TV set is shared with/owned by someone else —
   *  see EmoteSetOwnershipService, can never be fully complete (7TV has no reverse "who else has this
   *  set active" lookup). */
  getSetWarning(channelName: string): Observable<EmoteSetWarning> {
    return this.http.get<EmoteSetWarning>(`/api/channels/${channelName}/emotes/set-warning`);
  }

  /** Deliberately separate from ChannelService.getStatus (management-only): a 7TV editor without
   *  Twitch-mod status must still see this to render the mass-delete panel's "Löschen" button.
   *  Carries the slot budget and the tracking start too — same audience, same page, one request. */
  getSetStatus(channelName: string): Observable<EmoteSetStatus> {
    return this.http.get<EmoteSetStatus>(`/api/channels/${channelName}/emotes/active-set`);
  }

  /** Exact-name collisions in the channel's active 7TV set — while one exists, chat usage of the
   *  name is counted onto a single one of the emotes, distorting the usage numbers. Same audience
   *  as getSetStatus: fixing a collision happens on 7TV, which editors can do too. */
  getDuplicateNames(channelName: string): Observable<DuplicateEmoteName[]> {
    return this.http.get<DuplicateEmoteName[]>(
      `/api/channels/${channelName}/emotes/duplicate-names`,
    );
  }
}
