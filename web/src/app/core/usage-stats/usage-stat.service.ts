import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';

import { EmoteUsageTotal } from './usage-stat.model';

@Injectable({ providedIn: 'root' })
export class UsageStatService {
  private readonly http = inject(HttpClient);

  getTotals(channelName: string, from: string, to: string): Observable<EmoteUsageTotal[]> {
    return this.http.get<EmoteUsageTotal[]>(`/api/channels/${channelName}/usage-stats/totals`, {
      params: { from, to },
    });
  }
}
