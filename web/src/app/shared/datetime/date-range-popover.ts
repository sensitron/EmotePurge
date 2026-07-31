import { Component, ElementRef, computed, inject, model, signal, viewChild } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { LanguageService } from '../../core/i18n/language.service';
import { toLocale } from '../../core/i18n/locale';

/**
 * From/To date pair as a popover anchored to a trigger button, for the usage-stats "custom range"
 * preset. Inline date inputs used to push the rest of the filter toolbar onto a second row; the
 * popover keeps the toolbar single-line. Same panel contract as DateTimePicker: closes via the
 * explicit "Fertig"/"Done" button, a click anywhere outside the component, or Escape; value
 * changes apply immediately.
 *
 * The component is only rendered while the "custom" preset is active, so its creation *is* the
 * "user picked custom" moment — it starts open instead of taking an open-state input.
 */
@Component({
  selector: 'app-date-range-popover',
  imports: [TranslocoPipe],
  host: {
    '(document:click)': 'onDocumentClick($event)',
    '(keydown.escape)': 'onEscape()',
  },
  template: `
    <div class="relative inline-block">
      <button
        #trigger
        type="button"
        class="app-input-sm text-left"
        [attr.aria-expanded]="isOpen()"
        [attr.aria-label]="'dateRange.editRange' | transloco"
        (click)="toggle()"
      >
        {{ label() }}
      </button>

      @if (isOpen()) {
        <!-- z-30 inside the filter toolbar's z-20 sticky context — sanctioned by design doc §8.5
             (dropdowns opening out of a sticky bar inherit its context). -->
        <div
          class="absolute z-30 mt-1 w-64 max-w-[calc(100vw-2rem)] rounded-md border border-slate-700 bg-slate-900 p-3 shadow-xl"
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
              (click)="close()"
            >
              {{ 'datetimePicker.done' | transloco }}
            </button>
          </div>
        </div>
      }
    </div>
  `,
})
export class DateRangePopover {
  private readonly languageService = inject(LanguageService);
  private readonly elementRef = inject(ElementRef<HTMLElement>);
  private readonly trigger = viewChild.required<ElementRef<HTMLButtonElement>>('trigger');

  /** ISO date-only strings (`yyyy-MM-dd`), same format the native date inputs speak. */
  readonly from = model.required<string>();
  readonly to = model.required<string>();

  protected readonly isOpen = signal(true);

  protected readonly label = computed(() => {
    const locale = toLocale(this.languageService.lang());
    return `${this.formatDate(this.from(), locale)} – ${this.formatDate(this.to(), locale)}`;
  });

  private formatDate(iso: string, locale: string): string {
    // 'T00:00:00' forces local-time parsing — a bare ISO date is parsed as UTC midnight, which
    // renders as the previous day in every timezone west of Greenwich.
    return new Date(`${iso}T00:00:00`).toLocaleDateString(locale, { dateStyle: 'short' });
  }

  protected toggle(): void {
    this.isOpen.update((open) => !open);
  }

  protected close(): void {
    this.isOpen.set(false);
  }

  protected onDocumentClick(event: Event): void {
    if (this.isOpen() && !this.elementRef.nativeElement.contains(event.target as Node)) {
      this.close();
    }
  }

  protected onEscape(): void {
    if (this.isOpen()) {
      this.close();
      this.trigger().nativeElement.focus();
    }
  }
}
