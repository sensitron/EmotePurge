import { DOCUMENT } from '@angular/common';
import {
  Component,
  ElementRef,
  computed,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { Button } from '../ui/button';
import { Popover } from '../ui/popover';

export interface UsageRange {
  min: number | null;
  max: number | null;
}

type UsageRangePreset = 'all' | 'unused' | 'custom';

interface PresetOption {
  value: UsageRangePreset;
  labelKey: string;
}

const PRESET_OPTIONS: PresetOption[] = [
  { value: 'all', labelKey: 'usageRange.presetAll' },
  { value: 'unused', labelKey: 'usageRange.presetUnused' },
  { value: 'custom', labelKey: 'usageRange.presetCustom' },
];

/** Parses a number field's value; anything unparseable clears the bound rather than guessing. */
function parseBound(value: string): number | null {
  const trimmed = value.trim();
  if (trimmed === '') {
    return null;
  }
  const parsed = Number(trimmed);
  return Number.isNaN(parsed) ? null : parsed;
}

/**
 * The usage-count filter as a single toolbar slot, built after the DateRangeMenu sitting next to it.
 *
 * It replaces three controls that were really one filter. `Min`, `Max` and a "unused only (0×)"
 * toggle stood side by side, and the toggle was not a filter of its own at all — it *was* min = 0
 * and max = 0, silently overwriting the two fields beside it. Both directions of that coupling were
 * correct and unguessable: typing 0 into both fields lit the toggle up, and pressing the toggle and
 * then typing into Min switched it off again.
 *
 * As one control the coupling stops being a secret — "never used" is simply one of the named ranges
 * — and the two cryptic fields are no longer the toolbar's permanent first impression. The trigger
 * states the current setting, which is also the only place it was ever legible once the toolbar
 * scrolled away.
 *
 * Arrow keys move focus without selecting, matching the DateRangeMenu. There it avoids a refetch per
 * keypress; here every commit runs the host's `onChange`, which clears the emote selection — walking
 * the list with the keyboard would throw away the marks a user had just made.
 */
@Component({
  selector: 'app-usage-range-menu',
  imports: [Button, Popover, TranslocoPipe],
  template: `
    <div class="relative" data-popover-anchor>
      <button
        #trigger
        type="button"
        appButton="neutral"
        class="whitespace-nowrap"
        aria-haspopup="dialog"
        [attr.aria-expanded]="isOpen()"
        (click)="toggle()"
      >
        {{ 'usageRange.label' | transloco }}: {{ triggerValue() | transloco: triggerParams() }}
        <span aria-hidden="true" class="ml-1 text-fg-muted">▾</span>
      </button>

      @if (isOpen()) {
        <app-popover [ariaLabel]="'usageRange.menuLabel' | transloco" (closed)="close()">
          <div
            role="radiogroup"
            [attr.aria-label]="'usageRange.label' | transloco"
            class="flex flex-col gap-0.5 p-1"
          >
            <!-- min-h-11 on touch, tighter from sm up (§10: 44 px comfort target for pointer-coarse
                 rows, 24 px is only the floor). -->
            @for (option of presetOptions; track option.value; let index = $index) {
              <button
                type="button"
                role="radio"
                [attr.aria-checked]="preset() === option.value"
                [tabindex]="tabIndexFor(index)"
                [class]="
                  'flex min-h-11 items-center justify-between gap-3 rounded px-3 text-left text-sm transition sm:min-h-9 ' +
                  (preset() === option.value
                    ? 'bg-accent-selected font-medium text-on-accent'
                    : 'text-fg-body hover:bg-surface-inset')
                "
                (click)="select(option.value)"
                (keydown)="onKeydown($event, index)"
              >
                <span>{{ option.labelKey | transloco }}</span>
                @if (preset() === option.value) {
                  <span aria-hidden="true">✓</span>
                }
              </button>
            }
          </div>

          <!-- Same panel, swapped contents, exactly as in DateRangeMenu: there is only ever one
               surface on screen to dismiss. -->
          @if (preset() === 'custom') {
            <div class="border-t border-border p-3">
              <label for="usage-range-min" class="block text-xs text-fg-muted">
                {{ 'usageRange.min' | transloco }}
              </label>
              <input
                id="usage-range-min"
                type="number"
                min="0"
                [value]="min() ?? ''"
                (change)="setMin($any($event.target).value)"
                class="app-input-sm mt-1 w-full"
              />
              <label for="usage-range-max" class="mt-3 block text-xs text-fg-muted">
                {{ 'usageRange.max' | transloco }}
              </label>
              <input
                id="usage-range-max"
                type="number"
                min="0"
                [value]="max() ?? ''"
                (change)="setMax($any($event.target).value)"
                class="app-input-sm mt-1 w-full"
              />
              <div class="mt-3 flex justify-end">
                <button type="button" appButton="primary" (click)="close()">
                  {{ 'datetimePicker.done' | transloco }}
                </button>
              </div>
            </div>
          }
        </app-popover>
      }
    </div>
  `,
})
export class UsageRangeMenu {
  readonly min = input.required<number | null>();
  readonly max = input.required<number | null>();
  readonly rangeChange = output<UsageRange>();

  private readonly document = inject(DOCUMENT);
  private readonly elementRef = inject(ElementRef<HTMLElement>);
  private readonly trigger = viewChild<ElementRef<HTMLButtonElement>>('trigger');

  protected readonly presetOptions = PRESET_OPTIONS;
  protected readonly isOpen = signal(false);
  // Roving tabindex: null means "follow the selection", which is where focus should land the first
  // time the group is tabbed into.
  private readonly focusedIndex = signal<number | null>(null);
  /**
   * "Custom" cannot be derived from the bounds alone: an empty custom range looks exactly like
   * "all", so choosing it would immediately bounce the selection back and hide the fields the user
   * just asked for. Sticky until another preset is picked, so reopening the menu still shows them.
   */
  private readonly customRequested = signal(false);

  protected readonly preset = computed<UsageRangePreset>(() => {
    if (this.customRequested()) {
      return 'custom';
    }
    const min = this.min();
    const max = this.max();
    if (min === null && max === null) {
      return 'all';
    }
    return min === 0 && max === 0 ? 'unused' : 'custom';
  });

  /** Translation key for the trigger — the current setting, not the control's name. */
  protected readonly triggerValue = computed(() => {
    const min = this.min();
    const max = this.max();
    if (min === null && max === null) {
      return 'usageRange.presetAll';
    }
    if (min === 0 && max === 0) {
      return 'usageRange.presetUnused';
    }
    if (min !== null && max !== null) {
      return 'usageRange.valueBetween';
    }
    return min !== null ? 'usageRange.valueAtLeast' : 'usageRange.valueUpTo';
  });

  protected readonly triggerParams = computed(() => ({ min: this.min(), max: this.max() }));

  protected toggle(): void {
    if (this.isOpen()) {
      this.close();
      return;
    }
    this.isOpen.set(true);
  }

  protected close(): void {
    if (!this.isOpen()) {
      return;
    }
    // Focus would otherwise fall to <body> together with the panel that held it.
    const hadFocus = this.elementRef.nativeElement.contains(this.document.activeElement);
    this.isOpen.set(false);
    this.focusedIndex.set(null);
    if (hadFocus) {
      this.trigger()?.nativeElement.focus();
    }
  }

  protected select(value: UsageRangePreset): void {
    if (value === 'custom') {
      this.customRequested.set(true);
      // Stays open: the fields the user just asked for are inside this panel.
      this.focusedIndex.set(PRESET_OPTIONS.length - 1);
      return;
    }

    this.customRequested.set(false);
    this.rangeChange.emit(value === 'unused' ? { min: 0, max: 0 } : { min: null, max: null });
    this.close();
  }

  protected setMin(value: string): void {
    this.rangeChange.emit({ min: parseBound(value), max: this.max() });
  }

  protected setMax(value: string): void {
    this.rangeChange.emit({ min: this.min(), max: parseBound(value) });
  }

  protected tabIndexFor(index: number): number {
    const active = this.focusedIndex() ?? this.selectedIndex();
    return index === active ? 0 : -1;
  }

  protected onKeydown(event: KeyboardEvent, index: number): void {
    let delta: number;
    switch (event.key) {
      case 'ArrowRight':
      case 'ArrowDown':
        delta = 1;
        break;
      case 'ArrowLeft':
      case 'ArrowUp':
        delta = -1;
        break;
      default:
        return;
    }

    event.preventDefault();
    // Focus only — see the class comment: committing here would clear the emote selection on every
    // keypress. Enter/Space fire click and go through select().
    const next = (index + delta + PRESET_OPTIONS.length) % PRESET_OPTIONS.length;
    this.focusedIndex.set(next);
    const group = (event.currentTarget as HTMLElement).closest('[role="radiogroup"]');
    group?.querySelectorAll<HTMLButtonElement>('[role="radio"]')[next]?.focus();
  }

  private selectedIndex(): number {
    const index = PRESET_OPTIONS.findIndex((option) => option.value === this.preset());
    return index === -1 ? 0 : index;
  }
}
