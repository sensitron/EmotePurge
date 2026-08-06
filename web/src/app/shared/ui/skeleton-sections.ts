import { Component, computed, input } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

const PAIRS_PER_SECTION = 4;

/**
 * Skeleton for the admin detail pages, whose loaded state is not a list at all but a stack of
 * rule-separated sections, each a heading plus a two-column definition list (monitoring,
 * channel detail). They borrowed the row skeleton for want of anything closer and got an outline
 * that shared nothing with what arrived; this mirrors the real section, down to the dt/dd pair
 * lying on one line on phones and stacking from sm up.
 *
 * The shimmer blocks are decorative — screen readers get one status element with a translated
 * "loading" label instead, same pattern as the row and atlas skeletons (design doc 6.1).
 */
@Component({
  selector: 'app-skeleton-sections',
  imports: [TranslocoPipe],
  template: `
    <div role="status" [attr.aria-label]="'common.loading' | transloco" class="flex flex-col gap-6">
      @for (section of sections(); track section) {
        <section class="flex flex-col gap-3 border-t border-border pt-4" aria-hidden="true">
          <div class="flex flex-wrap items-center justify-between gap-3">
            <div class="app-skeleton h-5 w-40 max-w-full"></div>
            <div class="app-skeleton h-4 w-20 shrink-0"></div>
          </div>
          <div class="grid grid-cols-1 gap-x-6 gap-y-2 sm:grid-cols-2">
            @for (pair of pairs; track pair) {
              <div class="flex justify-between gap-4 sm:block">
                <div class="app-skeleton h-3 w-28 max-w-full"></div>
                <div class="app-skeleton h-3 w-20 max-w-full sm:mt-1.5"></div>
              </div>
            }
          </div>
        </section>
      }
    </div>
  `,
})
export class SkeletonSections {
  readonly count = input(3);

  protected readonly sections = computed(() => Array.from({ length: this.count() }, (_, i) => i));
  protected readonly pairs = Array.from({ length: PAIRS_PER_SECTION }, (_, i) => i);
}
