import { Component, ElementRef, inject, model, output } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

/**
 * From/To date pair as a popover for the usage-stats "custom range" preset. Deliberately
 * trigger-less: it anchors below the segmented control's "custom" segment instead of adding its
 * own toolbar element, so activating the custom range can never make the filter toolbar wrap —
 * in any locale, at any viewport width (an earlier inline variant pushed the German toolbar onto
 * a second row). Reopening is the anchor's job (SegmentedControl's `reselected` output).
 *
 * Contract with the host page: render inside a `position: relative` wrapper that also contains
 * the anchor control and is marked `data-date-range-anchor` — clicks inside that wrapper (e.g. on
 * the segment that just opened the popover) don't count as outside-clicks. Rendered = open; the
 * component closes itself only by emitting `closed` (outside click, Escape, done button), the
 * host owns the visibility state. Same panel contract as DateTimePicker otherwise: value changes
 * apply immediately.
 */
@Component({
  selector: 'app-date-range-popover',
  imports: [TranslocoPipe],
  host: {
    '(document:click)': 'onDocumentClick($event)',
    '(document:keydown.escape)': 'onEscape()',
  },
  template: `
    <!-- z-30 inside the filter toolbar's z-20 sticky context — sanctioned by design doc §8.5
         (dropdowns opening out of a sticky bar inherit its context). -->
    <div
      role="dialog"
      [attr.aria-label]="'dateRange.editRange' | transloco"
      class="absolute top-full left-0 z-30 mt-1 w-64 max-w-[calc(100vw-2rem)] rounded-md border border-slate-700 bg-slate-900 p-3 shadow-xl"
    >
      <label for="date-range-from" class="block text-xs text-slate-400">{{
        'dateRange.from' | transloco
      }}</label>
      <input
        id="date-range-from"
        type="date"
        [value]="from()"
        (change)="from.set($any($event.target).value)"
        class="app-input-sm mt-1 w-full"
      />
      <label for="date-range-to" class="mt-3 block text-xs text-slate-400">{{
        'dateRange.to' | transloco
      }}</label>
      <input
        id="date-range-to"
        type="date"
        [value]="to()"
        (change)="to.set($any($event.target).value)"
        class="app-input-sm mt-1 w-full"
      />
      <div class="mt-3 flex justify-end">
        <button
          type="button"
          class="rounded-md bg-purple-600 px-3 py-1.5 text-sm text-white hover:bg-purple-500"
          (click)="closed.emit()"
        >
          {{ 'datetimePicker.done' | transloco }}
        </button>
      </div>
    </div>
  `,
})
export class DateRangePopover {
  private readonly elementRef = inject(ElementRef<HTMLElement>);

  /** ISO date-only strings (`yyyy-MM-dd`), same format the native date inputs speak. */
  readonly from = model.required<string>();
  readonly to = model.required<string>();

  readonly closed = output<void>();

  protected onDocumentClick(event: Event): void {
    // The anchor wrapper (segmented control + this popover) counts as inside: the click that
    // opens the popover bubbles to this document listener in the same dispatch, and re-clicking
    // "custom" to reopen must not immediately close it again.
    const anchor =
      this.elementRef.nativeElement.closest('[data-date-range-anchor]') ??
      this.elementRef.nativeElement;
    if (!anchor.contains(event.target as Node)) {
      this.closed.emit();
    }
  }

  protected onEscape(): void {
    this.closed.emit();
  }
}
