import { Component, computed, inject, input, output } from '@angular/core';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';

import { DeleteQueueItem, SyncReportState } from '../../core/seven-tv/seven-tv-delete.service';

@Component({
  selector: 'app-delete-progress-panel',
  imports: [TranslocoPipe],
  template: `
    <div class="rounded-md bg-slate-800 px-4 py-3" role="status">
      <div class="mb-2 flex items-center justify-between text-sm">
        <span>{{
          'massDelete.progress' | transloco: { finished: finished(), total: total() }
        }}</span>
        @if (isRunning()) {
          <button type="button" class="text-red-400 hover:underline" (click)="cancelled.emit()">
            {{ 'common.cancel' | transloco }}
          </button>
        } @else {
          <!-- h-7 + px-2 keeps this above the 24px target floor (the audit gate counts every button). -->
          <button
            type="button"
            class="flex h-7 items-center rounded px-2 text-slate-400 transition hover:bg-slate-700 hover:text-slate-200"
            (click)="dismissed.emit()"
          >
            {{ 'common.close' | transloco }}
          </button>
        }
      </div>
      <div class="h-2 w-full overflow-hidden rounded-full bg-slate-700">
        <div class="h-full bg-purple-500 transition-all" [style.width.%]="progressPercent()"></div>
      </div>

      @if (failedItems().length > 0) {
        <ul class="mt-3 space-y-1 text-sm text-red-400" role="alert">
          @for (item of failedItems(); track item.emoteId) {
            <li>{{ item.name }}: {{ item.errorMessage ?? deleteFailedFallback() }}</li>
          }
        </ul>
      }

      @if (syncReportFailed()) {
        <div
          class="mt-3 rounded-md border border-amber-800 bg-amber-950/40 px-3 py-2 text-sm"
          role="alert"
        >
          <p class="font-medium text-amber-300">{{ 'massDelete.syncFailedTitle' | transloco }}</p>
          <p class="mt-1 text-amber-200">{{ 'massDelete.syncFailed' | transloco }}</p>
          <button
            type="button"
            class="mt-2 text-amber-300 hover:underline"
            (click)="syncRetryRequested.emit()"
          >
            {{ 'massDelete.syncRetry' | transloco }}
          </button>
        </div>
      } @else if (syncReport() === 'succeeded' && !isRunning()) {
        <p class="mt-3 text-sm text-slate-400">{{ 'massDelete.syncRetrySucceeded' | transloco }}</p>
      }
    </div>
  `,
})
export class DeleteProgressPanel {
  readonly items = input.required<DeleteQueueItem[]>();
  readonly isRunning = input.required<boolean>();
  readonly syncReport = input.required<SyncReportState>();
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

  // 'partial' shares the notice with 'failed': in both cases the backend's view of the set differs
  // from what was actually deleted, and the remedy (retry, or wait for the periodic resync) is the same.
  protected readonly syncReportFailed = computed(
    () => this.syncReport() === 'failed' || this.syncReport() === 'partial',
  );

  protected deleteFailedFallback(): string {
    return this.translocoService.translate('massDelete.deleteFailedFallback');
  }
}
