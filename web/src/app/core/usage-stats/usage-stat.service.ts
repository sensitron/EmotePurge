import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable, shareReplay } from 'rxjs';

import { EmoteUsageSeries, EmoteUsageTotal } from './usage-stat.model';

@Injectable({ providedIn: 'root' })
export class UsageStatService {
  private readonly http = inject(HttpClient);

  /**
   * Series cache per (channel, emote, range): /daily shares the ExternalApi budget (40/min) with
   * the totals reload, and a drilldown opened twice is the same request. Entries live until
   * clearSeriesCache() — the pages call it when the channel or date range changes.
   */
  private readonly seriesCache = new Map<string, Observable<EmoteUsageSeries>>();

  getTotals(channelName: string, from: string, to: string): Observable<EmoteUsageTotal[]> {
    return this.http.get<EmoteUsageTotal[]>(`/api/channels/${channelName}/usage-stats/totals`, {
      params: { from, to },
    });
  }

  getDailySeries(
    channelName: string,
    emoteId: string,
    from: string,
    to: string,
  ): Observable<EmoteUsageSeries> {
    const key = `${channelName}|${emoteId}|${from}|${to}`;
    let series$ = this.seriesCache.get(key);
    if (!series$) {
      series$ = this.http
        .get<EmoteUsageSeries>(`/api/channels/${channelName}/usage-stats/daily`, {
          params: { emoteId, from, to },
        })
        // refCount:false keeps the replayed value alive with no subscriber — that is the cache.
        .pipe(shareReplay({ bufferSize: 1, refCount: false }));
      this.seriesCache.set(key, series$);
      // A failed request must not stick as a cached error — the next open should retry.
      series$.subscribe({ error: () => this.seriesCache.delete(key) });
    }
    return series$;
  }

  clearSeriesCache(): void {
    this.seriesCache.clear();
  }
}
