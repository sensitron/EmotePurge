import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';

import { PagedResult } from '../models/paged-result.model';
import {
  AdminChannel,
  AdminChannelDetail,
  AdminHealth,
  AdminRoster,
  AdminUser,
  AuditLogEntry,
  AuditLogFilter,
} from './admin.model';

/**
 * The /api/admin/* client. Every endpoint here is global-admin-only server-side
 * (GlobalAdminAuthorizationFilter on the route group); the frontend's adminGuard is visibility only.
 */
@Injectable({ providedIn: 'root' })
export class AdminService {
  private readonly http = inject(HttpClient);

  getHealth(): Observable<AdminHealth> {
    return this.http.get<AdminHealth>('/api/admin/health');
  }

  /** Every tracked channel with its emote/vote-session aggregates. The single global channel list
   *  since the overview's admin section (GET /api/channels) was removed. */
  listChannels(): Observable<AdminChannel[]> {
    return this.http.get<AdminChannel[]>('/api/admin/channels');
  }

  /** The worker's per-channel roster next to the channels the database considers active. Separate
   *  from getHealth() because the roster is published on a 60 s cadence, a third of the health
   *  snapshot's — folding it in would refetch it twice for nothing. */
  getRoster(): Observable<AdminRoster> {
    return this.http.get<AdminRoster>('/api/admin/roster');
  }

  /** One channel's database row plus what the worker currently believes about it — the support
   *  drilldown behind "why isn't my channel syncing?". */
  getChannel(channelName: string): Observable<AdminChannelDetail> {
    return this.http.get<AdminChannelDetail>(
      `/api/admin/channels/${encodeURIComponent(channelName)}`,
    );
  }

  /** Asks the worker to re-read the channel's 7TV emote set right now instead of waiting for the
   *  periodic resync. Answers 202: the command is published to Redis, so "accepted" is all the Api
   *  can honestly promise — the caller must not present it as a finished sync. */
  resyncChannel(channelName: string): Observable<void> {
    return this.http.post<void>(
      `/api/admin/channels/${encodeURIComponent(channelName)}/resync`,
      null,
    );
  }

  /** Every user who ever logged in, most recently seen first. Paged: unlike channels, this list
   *  is not capped by anything. */
  listUsers(page = 1, pageSize = 25): Observable<PagedResult<AdminUser>> {
    return this.http.get<PagedResult<AdminUser>>('/api/admin/users', {
      params: { page, pageSize },
    });
  }

  /** Forced logout for one user: server-side session revocation plus dropping their stored Twitch
   *  tokens — mirrors what POST /api/auth/logout does for oneself, and is audited server-side. */
  revokeSessions(twitchUserId: string): Observable<void> {
    return this.http.post<void>(
      `/api/admin/users/${encodeURIComponent(twitchUserId)}/revoke-sessions`,
      null,
    );
  }

  /** Drops every cached role answer (mod/sub/7TV editor) of one user, so the next role check
   *  resolves live against Twitch/7TV. Returns how many Redis entries were removed — the only
   *  visible effect of an action that otherwise changes nothing an admin could see. */
  invalidateRoleCache(twitchUserId: string): Observable<{ removedEntries: number }> {
    return this.http.post<{ removedEntries: number }>(
      `/api/admin/users/${encodeURIComponent(twitchUserId)}/invalidate-role-cache`,
      null,
    );
  }

  /** Every audited action, newest first. Paged rather than capped: retention is unbounded by
   *  decision, so this list only grows. Filter fields are sent only when non-blank, so an empty
   *  toolbar produces the same request (and cache key) as no toolbar at all. */
  listAuditLog(
    page = 1,
    pageSize = 25,
    filter?: AuditLogFilter,
  ): Observable<PagedResult<AuditLogEntry>> {
    const params: Record<string, string | number> = { page, pageSize };
    if (filter?.action) {
      params['action'] = filter.action;
    }
    if (filter?.channel?.trim()) {
      params['channel'] = filter.channel.trim();
    }
    if (filter?.actor?.trim()) {
      params['actor'] = filter.actor.trim();
    }
    return this.http.get<PagedResult<AuditLogEntry>>('/api/admin/audit-log', { params });
  }
}
