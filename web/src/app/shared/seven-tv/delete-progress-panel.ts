import { Component, computed, inject, input, output } from '@angular/core';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';

import { DeleteQueueItem } from '../../core/seven-tv/seven-tv-delete.service';

@Component({
  selector: 'app-delete-progress-panel',
  imports: [TranslocoPipe],
  template: `
    <div class="rounded-md bg-slate-800 px-4 py-3">
      <div class="mb-2 flex items-center justify-between text-sm">
        <span>{{ 'massDelete.progress' | transloco: { finished: finished(), total: total() } }}</span>
        @if (isRunning()) {
          <button type="button" class="text-red-400 hover:underline" (click)="cancelled.emit()">
            {{ 'common.cancel' | transloco }}
          </button>
        }
      </div>
      <div class="h-2 w-full overflow-hidden rounded-full bg-slate-700">
        <div class="h-full bg-purple-500 transition-all" [style.width.%]="progressPercent()"></div>
      </div>

      @if (failedItems().length > 0) {
        <ul class="mt-3 space-y-1 text-sm text-red-400">
          @for (item of failedItems(); track item.emoteId) {
            <li>{{ item.name }}: {{ item.errorMessage ?? deleteFailedFallback() }}</li>
          }
        </ul>
      }
    </div>
  `,
})
export class DeleteProgressPanel {
  readonly items = input.required<DeleteQueueItem[]>();
  readonly isRunning = input.required<boolean>();
  readonly cancelled = output<void>();

  private readonly translocoService = inject(TranslocoService);

  protected readonly total = computed(() => this.items().length);
  protected readonly finished = computed(
    () => this.items().filter((item) => item.status === 'done' || item.status === 'failed').length,
  );
  protected readonly progressPercent = computed(() => (this.total() === 0 ? 0 : (this.finished() / this.total()) * 100));
  protected readonly failedItems = computed(() => this.items().filter((item) => item.status === 'failed'));

  protected deleteFailedFallback(): string {
    return this.translocoService.translate('massDelete.deleteFailedFallback');
  }
}
