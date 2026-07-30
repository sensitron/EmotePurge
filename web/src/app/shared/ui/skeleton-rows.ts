import { Component, computed, input } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

/**
 * Card-row skeleton for list pages while their first load is in flight (NN/g: skeleton for page
 * loads, spinner only for isolated actions). The shimmer blocks are decorative — screen readers
 * get one status element with a translated "loading" label instead.
 */
@Component({
  selector: 'app-skeleton-rows',
  imports: [TranslocoPipe],
  template: `
    <div role="status" [attr.aria-label]="'common.loading' | transloco">
      <ul class="flex flex-col gap-2" aria-hidden="true">
        @for (row of rows(); track row) {
          <li class="app-card flex items-center justify-between gap-4 px-4 py-3">
            <div class="flex min-w-0 flex-1 flex-col gap-2">
              <div class="app-skeleton h-4 w-40 max-w-full"></div>
              <div class="app-skeleton h-3 w-24 max-w-full"></div>
            </div>
            <div class="app-skeleton h-8 w-20 shrink-0"></div>
          </li>
        }
      </ul>
    </div>
  `,
})
export class SkeletonRows {
  readonly count = input(3);

  protected readonly rows = computed(() => Array.from({ length: this.count() }, (_, i) => i));
}
