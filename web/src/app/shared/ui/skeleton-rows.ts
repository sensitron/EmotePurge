import { Component, computed, input } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

/**
 * Ruled-row skeleton for list pages while their first load is in flight (NN/g: skeleton for page
 * loads, spinner only for isolated actions). The shimmer blocks are decorative — screen readers
 * get one status element with a translated "loading" label instead.
 *
 * The container repeats the ruled-row recipe of the lists it stands in for (design doc 2.1), down
 * to the negative margin: a skeleton that draws a different outline than the content replacing it
 * makes the arrival of that content read as a layout error. This was a stack of bordered cards
 * until the rows lost their cards, and the mismatch showed as text jumping sideways on every load.
 */
@Component({
  selector: 'app-skeleton-rows',
  imports: [TranslocoPipe],
  template: `
    <div role="status" [attr.aria-label]="'common.loading' | transloco">
      <ul class="-mx-3 divide-y divide-border border-y border-border" aria-hidden="true">
        @for (row of rows(); track row) {
          <li class="flex items-center justify-between gap-4 px-3 py-3">
            <div class="flex min-w-0 flex-1 flex-col gap-1.5">
              <div class="app-skeleton h-4 w-40 max-w-full"></div>
              <div class="app-skeleton h-3 w-24 max-w-full"></div>
            </div>
            <div class="app-skeleton h-4 w-16 shrink-0"></div>
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
