import { Component, ElementRef, computed, inject, input, model, signal } from '@angular/core';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';

import { LanguageService } from '../../core/i18n/language.service';
import { toLocale } from '../../core/i18n/locale';

function pad(n: number): string {
  return n.toString().padStart(2, '0');
}

function toDateOnlyString(date: Date): string {
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

function toTimeOnlyString(date: Date): string {
  return `${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

function toDateTimeLocalValue(date: Date): string {
  return `${toDateOnlyString(date)}T${toTimeOnlyString(date)}`;
}

function parseDateTimeLocal(value: string): Date | null {
  if (!value) {
    return null;
  }
  const [datePart, timePart] = value.split('T');
  const [year, month, day] = datePart.split('-').map(Number);
  const [hour, minute] = (timePart ?? '00:00').split(':').map(Number);
  return new Date(year, month - 1, day, hour, minute);
}

function isSameDay(a: Date, b: Date): boolean {
  return (
    a.getFullYear() === b.getFullYear() &&
    a.getMonth() === b.getMonth() &&
    a.getDate() === b.getDate()
  );
}

function startOfMonth(date: Date): Date {
  return new Date(date.getFullYear(), date.getMonth(), 1);
}

interface CalendarDay {
  date: Date;
  dayOfMonth: number;
  inCurrentMonth: boolean;
  isDisabled: boolean;
  isSelected: boolean;
  isToday: boolean;
}

const WEEKDAY_LABEL_KEYS = [
  'datetimePicker.weekdays.mon',
  'datetimePicker.weekdays.tue',
  'datetimePicker.weekdays.wed',
  'datetimePicker.weekdays.thu',
  'datetimePicker.weekdays.fri',
  'datetimePicker.weekdays.sat',
  'datetimePicker.weekdays.sun',
];

/**
 * Self-built calendar-grid + time popup, used in place of a bare native `<input type="datetime-local">`.
 * Value is kept in the same "datetime-local" string format (`YYYY-MM-DDTHH:mm`, local time, empty
 * string = unset) so existing callers barely change. The panel closes via the explicit
 * "Fertig"/"Done" button, a click anywhere outside the component, or Escape.
 */
@Component({
  selector: 'app-datetime-picker',
  imports: [TranslocoPipe],
  host: {
    '(document:click)': 'onDocumentClick($event)',
    '(keydown.escape)': 'onEscape()',
  },
  template: `
    <div class="relative inline-block">
      <button
        type="button"
        class="rounded-md border border-slate-500 bg-slate-950 px-3 py-2 text-left text-sm text-slate-100 hover:border-slate-400"
        (click)="togglePanel()"
      >
        {{ displayValue() }}
      </button>

      @if (isOpen()) {
        <!-- z-30: must clear the session cards' secondary actions, which sit at relative z-10
             per the stretched-link contract (see .app-card-link) and come later in the DOM. -->
        <div
          class="absolute z-30 mt-1 w-64 rounded-md border border-slate-700 bg-slate-900 p-3 shadow-xl"
        >
          <div class="mb-2 flex items-center justify-between">
            <button
              type="button"
              class="rounded px-2 py-1 text-slate-400 hover:bg-slate-800"
              (click)="previousMonth()"
            >
              ‹
            </button>
            <span class="text-sm font-medium">{{ monthLabel() }}</span>
            <button
              type="button"
              class="rounded px-2 py-1 text-slate-400 hover:bg-slate-800"
              (click)="nextMonth()"
            >
              ›
            </button>
          </div>

          <div class="grid grid-cols-7 gap-1 text-center text-xs text-slate-400">
            @for (labelKey of weekdayLabelKeys; track labelKey) {
              <span>{{ labelKey | transloco }}</span>
            }
          </div>
          <div class="grid grid-cols-7 gap-1">
            @for (day of calendarDays(); track day.date.getTime()) {
              <button
                type="button"
                [disabled]="day.isDisabled"
                class="rounded p-1 text-xs transition"
                [class]="dayClass(day)"
                (click)="selectDay(day)"
              >
                {{ day.dayOfMonth }}
              </button>
            }
          </div>

          <input
            type="time"
            [value]="timeValue()"
            (change)="setTime($any($event.target).value)"
            class="mt-3 w-full rounded-md border border-slate-500 bg-slate-950 px-2 py-1.5 text-sm"
          />

          <div class="mt-3 flex items-center justify-between text-sm">
            <button type="button" class="text-slate-400 hover:underline" (click)="clear()">
              {{ 'datetimePicker.reset' | transloco }}
            </button>
            <button
              type="button"
              class="rounded-md bg-purple-600 px-3 py-1.5 text-white hover:bg-purple-500"
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
export class DateTimePicker {
  private readonly languageService = inject(LanguageService);
  private readonly translocoService = inject(TranslocoService);
  private readonly elementRef = inject(ElementRef<HTMLElement>);

  readonly value = model('');
  readonly max = input<string>();
  readonly placeholder = input<string>();

  protected readonly weekdayLabelKeys = WEEKDAY_LABEL_KEYS;
  protected readonly isOpen = signal(false);
  protected readonly viewMonth = signal(startOfMonth(new Date()));

  protected readonly displayValue = computed(() => {
    const parsed = parseDateTimeLocal(this.value());
    if (parsed) {
      return `${toDateOnlyString(parsed)} ${toTimeOnlyString(parsed)}`;
    }
    const lang = this.languageService.lang();
    return (
      this.placeholder() ?? this.translocoService.translate('datetimePicker.placeholder', {}, lang)
    );
  });

  protected readonly timeValue = computed(() => {
    const parsed = parseDateTimeLocal(this.value());
    return parsed ? toTimeOnlyString(parsed) : '00:00';
  });

  protected readonly monthLabel = computed(() =>
    this.viewMonth().toLocaleDateString(toLocale(this.languageService.lang()), {
      month: 'long',
      year: 'numeric',
    }),
  );

  protected readonly calendarDays = computed<CalendarDay[]>(() => {
    const month = this.viewMonth();
    const selected = parseDateTimeLocal(this.value());
    const maxRaw = this.max();
    const maxDay = maxRaw ? parseDateTimeLocal(maxRaw) : null;
    const maxDayOnly = maxDay
      ? new Date(maxDay.getFullYear(), maxDay.getMonth(), maxDay.getDate())
      : null;
    const today = new Date();
    today.setHours(0, 0, 0, 0);

    const firstOfMonth = new Date(month.getFullYear(), month.getMonth(), 1);
    // Monday-first grid — JS getDay() is 0=Sunday, shift so Monday=0.
    const leadingBlank = (firstOfMonth.getDay() + 6) % 7;
    const gridStart = new Date(firstOfMonth);
    gridStart.setDate(gridStart.getDate() - leadingBlank);

    const days: CalendarDay[] = [];
    for (let i = 0; i < 42; i++) {
      const date = new Date(gridStart);
      date.setDate(gridStart.getDate() + i);
      date.setHours(0, 0, 0, 0);

      days.push({
        date,
        dayOfMonth: date.getDate(),
        inCurrentMonth: date.getMonth() === month.getMonth(),
        isDisabled: maxDayOnly !== null && date.getTime() > maxDayOnly.getTime(),
        isSelected: selected !== null && isSameDay(date, selected),
        isToday: isSameDay(date, today),
      });
    }
    return days;
  });

  protected togglePanel(): void {
    this.isOpen.update((open) => !open);
  }

  protected close(): void {
    this.isOpen.set(false);
  }

  protected onDocumentClick(event: Event): void {
    // Clicks inside the component (trigger button, calendar, time input) are handled by their own
    // handlers; anything outside dismisses the panel — same pattern as the shell's disclosure menu.
    if (this.isOpen() && !this.elementRef.nativeElement.contains(event.target as Node)) {
      this.close();
    }
  }

  protected onEscape(): void {
    this.close();
  }

  protected previousMonth(): void {
    this.viewMonth.update((month) => new Date(month.getFullYear(), month.getMonth() - 1, 1));
  }

  protected nextMonth(): void {
    this.viewMonth.update((month) => new Date(month.getFullYear(), month.getMonth() + 1, 1));
  }

  protected selectDay(day: CalendarDay): void {
    if (day.isDisabled) {
      return;
    }
    const current = parseDateTimeLocal(this.value()) ?? new Date();
    const updated = new Date(day.date);
    updated.setHours(current.getHours(), current.getMinutes(), 0, 0);
    this.value.set(toDateTimeLocalValue(updated));
  }

  protected setTime(time: string): void {
    const [hour, minute] = time.split(':').map(Number);
    const current = parseDateTimeLocal(this.value()) ?? new Date();
    current.setHours(hour, minute, 0, 0);
    this.value.set(toDateTimeLocalValue(current));
  }

  protected clear(): void {
    this.value.set('');
  }

  protected dayClass(day: CalendarDay): string {
    if (day.isDisabled) {
      return 'cursor-not-allowed text-slate-700';
    }
    if (day.isSelected) {
      return 'bg-purple-600 text-white';
    }
    if (!day.inCurrentMonth) {
      return 'text-slate-600 hover:bg-slate-800';
    }
    if (day.isToday) {
      return 'text-purple-400 hover:bg-slate-800';
    }
    return 'text-slate-200 hover:bg-slate-800';
  }
}
