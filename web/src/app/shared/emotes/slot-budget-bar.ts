import { Component, computed, input } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { SlotBudgetTone, slotBudget } from './slot-budget';

/**
 * The `dot` role: a small graphic that carries meaning on its own and therefore owes 3:1 against
 * the track, not 4,5:1. Amber is the one that needs a different step per mode — on a light track
 * amber-500 manages 2,0:1 and amber-600 only 2,9:1, so the light token drops to amber-700. The
 * numbers above the bar are text as well, so none of this is the sole carrier of the message.
 */
const TONE_CLASSES: Record<SlotBudgetTone, string> = {
  success: 'bg-success-dot',
  warning: 'bg-warning-dot',
  danger: 'bg-danger-dot',
};

/**
 * "847 of 1000 slots taken — 612 after this purge."
 *
 * Renders nothing at all when 7TV reported no capacity for the set: a bar without a trustworthy
 * denominator would be worse than none. Deliberately not part of the mass-delete panel — that one
 * also runs on the vote-session page, where the selected emotes are a ballot subset and their count
 * says nothing about the set's occupancy.
 */
@Component({
  selector: 'app-slot-budget-bar',
  imports: [TranslocoPipe],
  template: `
    @if (budget(); as slots) {
      <div class="flex flex-col gap-1">
        <div class="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-1 text-sm">
          <!-- The numbers are text as well, so the bar never carries meaning on its own. -->
          <span class="text-fg-secondary">
            {{
              'usageStats.slots.occupied'
                | transloco: { occupied: slots.occupied, capacity: slots.capacity }
            }}
          </span>
          @if (slots.hasPendingRemoval) {
            <span class="font-medium text-accent-fg">
              {{ 'usageStats.slots.afterDelete' | transloco: { projected: slots.projected } }}
            </span>
          }
        </div>
        <div
          class="flex h-2 w-full overflow-hidden rounded-full bg-surface-inset"
          role="progressbar"
          [attr.aria-valuenow]="slots.occupied"
          aria-valuemin="0"
          [attr.aria-valuemax]="slots.capacity"
          [attr.aria-label]="'usageStats.slots.barLabel' | transloco"
        >
          <div [class]="fillClass()" [style.width.%]="slots.projectedPercent"></div>
          <!-- The part the current selection would free, shown as a distinct segment rather than by
               shrinking the bar — the user has to see both states at once to judge the trade. -->
          @if (slots.hasPendingRemoval) {
            <div
              class="h-full bg-accent/50"
              [style.width.%]="slots.occupiedPercent - slots.projectedPercent"
            ></div>
          }
        </div>
      </div>
    }
  `,
})
export class SlotBudgetBar {
  /** `null` means 7TV reported no limit — the component then renders nothing. */
  readonly capacity = input.required<number | null>();
  readonly occupied = input.required<number>();

  /** How many of the occupied slots the pending action (a mass delete) would free. */
  readonly pendingRemoval = input(0);

  protected readonly budget = computed(() =>
    slotBudget(this.capacity(), this.occupied(), this.pendingRemoval()),
  );

  protected readonly fillClass = computed(() => {
    const tone = this.budget()?.tone ?? 'success';
    return `h-full ${TONE_CLASSES[tone]}`;
  });
}
