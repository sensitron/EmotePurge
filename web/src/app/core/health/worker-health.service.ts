import { HttpClient } from '@angular/common/http';
import { inject, Injectable, signal } from '@angular/core';
import { catchError, interval, of, startWith, switchMap } from 'rxjs';

import { WorkerHealthResponse } from './worker-health.model';

export type WorkerHealthStatus = 'connected' | 'stale' | 'unknown';

const POLL_INTERVAL_MS = 30_000;

@Injectable({ providedIn: 'root' })
export class WorkerHealthService {
  private readonly http = inject(HttpClient);

  readonly status = signal<WorkerHealthStatus>('unknown');

  constructor() {
    interval(POLL_INTERVAL_MS)
      .pipe(
        startWith(0),
        switchMap(() =>
          this.http.get<WorkerHealthResponse>('/api/worker/health').pipe(
            catchError(() => of<WorkerHealthResponse>({ status: 'unknown' })),
          ),
        ),
      )
      .subscribe((response) => {
        this.status.set(response.status === 'connected' ? 'connected' : response.status === 'disconnected' ? 'stale' : 'unknown');
      });
  }
}
