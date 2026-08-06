import { Component, computed, inject, input, output } from '@angular/core';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';

import { RunQueueItem } from '../../core/seven-tv/seven-tv-run-engine';
import { SyncReportState } from '../../core/seven-tv/seven-tv-delete.service';
import { Button } from '../ui/button';
import { NoticeBanner } from '../ui/notice-banner';

/** Renamed from DeleteProgressPanel when the restore run (A6) became its second consumer — the
 *  mechanics (bar, cancel, rate-limit countdown, failure list) are run-generic; only the wording
 *  differs, selected via `labelPrefix`. Dynamic Transloco keys follow the established
 *  `'prefix.' + key` pattern, and the input union keeps them findable. */
@Component({
  selector: 'app-run-progress-panel',
  imports: [Button, NoticeBanner, TranslocoPipe],
  template: `
    <div class="rounded-md bg-surface-inset px-4 py-3" role="status">
      <div class="mb-2 flex items-center justify-between text-sm">
        <span>{{
          labelPrefix() + '.progress' | transloco: { finished: finished(), total: total() }
        }}</span>
        @if (isRunning()) {
          <button type="button" appButton="danger-quiet" (click)="cancelled.emit()">
            {{ 'common.cancel' | transloco }}
          </button>
        } @else {
          <button type="button" appButton="neutral" (click)="dismissed.emit()">
            {{ 'common.close' | transloco }}
          </button>
        }
      </div>
      <!-- The track is one step further from the surface than the panel it sits in, so it stays
           visible whichever direction "further" means in the current mode. -->
      <div class="h-2 w-full overflow-hidden rounded-full bg-surface-inset-hover">
        <div class="h-full bg-accent transition-all" [style.width.%]="progressPercent()"></div>
      </div>

      <!-- Without this the bar just stops for up to a minute, which reads as a crash. -->
      @if (rateLimitPauseSeconds() !== null) {
        <p class="mt-2 text-sm text-warning-fg">
          {{ labelPrefix() + '.rateLimitPaused' | transloco: { seconds: rateLimitPauseSeconds() } }}
        </p>
      }

      @if (failedItems().length > 0) {
        <ul class="mt-3 space-y-1 text-sm text-danger-fg" role="alert">
          @for (item of failedItems(); track item.emoteId) {
            <li>{{ item.name }}: {{ item.errorMessage ?? failedFallback() }}</li>
          }
        </ul>
      }

      <!-- Post-run summary (A6): the counts as text, plus whatever run-scoped actions the host
           projects (protocol download, restore). Rendered only once the run has settled — during
           the run the bar and the failure list already say everything. -->
      @if (!isRunning() && total() > 0) {
        <div class="mt-3 flex flex-wrap items-center gap-x-3 gap-y-2 text-sm">
          <span class="text-fg-secondary">
            {{ labelPrefix() + '.summary.counts' | transloco: summaryCounts() }}
          </span>
          <ng-content select="[run-actions]" />
        </div>
      }

      @if (syncReportFailed()) {
        <app-notice-banner class="mt-3 block" variant="warning">
          <span class="flex flex-col gap-1">
            <span class="font-medium">{{ labelPrefix() + '.syncFailedTitle' | transloco }}</span>
            <span>{{ labelPrefix() + '.syncFailed' | transloco }}</span>
          </span>
          <button
            notice-action
            type="button"
            appButton="outline"
            (click)="syncRetryRequested.emit()"
          >
            {{ labelPrefix() + '.syncRetry' | transloco }}
          </button>
        </app-notice-banner>
      } @else if (syncReport() === 'succeeded' && !isRunning()) {
        <p class="mt-3 text-sm text-fg-muted">
          {{ labelPrefix() + '.syncRetrySucceeded' | transloco }}
        </p>
      }
    </div>
  `,
})
export class RunProgressPanel {
  readonly items = input.required<RunQueueItem[]>();
  readonly isRunning = input.required<boolean>();
  /** Which wording family the panel speaks — the union keeps the dynamic keys findable. */
  readonly labelPrefix = input<'massDelete' | 'restore'>('massDelete');
  /** State of the run's closing bookkeeping call (sync-deleted / sync-restored). Defaults to the
   *  state that renders nothing; the notice wording follows labelPrefix. */
  readonly syncReport = input<SyncReportState>('idle');
  /** Seconds left on a 7TV rate-limit pause, null while running normally. */
  readonly rateLimitPauseSeconds = input<number | null>(null);
  readonly cancelled = output<void>();
  readonly dismissed = output<void>();
  readonly syncRetryRequested = output<void>();

  private readonly translocoService = inject(TranslocoService);

  protected readonly total = computed(() => this.items().length);
  protected readonly finished = computed(
    () => this.items().filter((item) => item.status === 'done' || item.status === 'failed').length,
  );
  protected readonly progressPercent = computed(() =>
    this.total() === 0 ? 0 : (this.finished() / this.total()) * 100,
  );
  protected readonly failedItems = computed(() =>
    this.items().filter((item) => item.status === 'failed'),
  );

  protected readonly summaryCounts = computed(() => {
    const statuses = this.items().map((item) => item.status);
    return {
      done: statuses.filter((status) => status === 'done').length,
      failed: statuses.filter((status) => status === 'failed').length,
      cancelled: statuses.filter((status) => status === 'cancelled').length,
    };
  });

  // 'partial' shares the notice with 'failed': in both cases the backend's view of the set differs
  // from what was actually deleted, and the remedy (retry, or wait for the periodic resync) is the same.
  protected readonly syncReportFailed = computed(
    () => this.syncReport() === 'failed' || this.syncReport() === 'partial',
  );

  protected failedFallback(): string {
    return this.translocoService.translate(`${this.labelPrefix()}.deleteFailedFallback`);
  }
}
