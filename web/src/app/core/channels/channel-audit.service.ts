import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';

import { AuditLogEntry } from '../audit/audit.model';
import { PagedResult } from '../models/paged-result.model';

/** Optional narrowing of a channel's activity feed; fields are AND-combined server-side. */
export interface ChannelAuditFilter {
  action?: string;
  actor?: string;
}

/**
 * The channel-scoped audit log. Deliberately not a method on `AdminService`: that one is documented
 * as the global-admin client, and this endpoint answers for a channel's own management team.
 *
 * There is no `channel` parameter by design — the server takes it from the route and ignores
 * anything else, which is what keeps one channel's manager from reading another channel's log.
 */
@Injectable({ providedIn: 'root' })
export class ChannelAuditService {
  private readonly http = inject(HttpClient);

  listAuditLog(
    channelName: string,
    page = 1,
    pageSize = 25,
    filter: ChannelAuditFilter = {},
  ): Observable<PagedResult<AuditLogEntry>> {
    let params = new HttpParams().set('page', page).set('pageSize', pageSize);
    if (filter.action) {
      params = params.set('action', filter.action);
    }
    if (filter.actor) {
      params = params.set('actor', filter.actor);
    }

    return this.http.get<PagedResult<AuditLogEntry>>(`/api/channels/${channelName}/audit-log`, {
      params,
    });
  }
}
