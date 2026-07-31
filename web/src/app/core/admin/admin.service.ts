import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';

import { PagedResult } from '../models/paged-result.model';
import { AdminChannel, AdminHealth, AuditLogEntry } from './admin.model';

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

  /** Every audited action, newest first. Paged rather than capped: retention is unbounded by
   *  decision, so this list only grows. */
  listAuditLog(page = 1, pageSize = 25): Observable<PagedResult<AuditLogEntry>> {
    return this.http.get<PagedResult<AuditLogEntry>>('/api/admin/audit-log', {
      params: { page, pageSize },
    });
  }
}
