import { Component, input, model } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

export interface SegmentedControlOption {
  value: string;
  labelKey: string;
}

/**
 * Single-select segmented control for a small set of mutually exclusive options (time-range
 * presets etc.). Radiogroup semantics with a roving tabindex: the group is one tab stop,
 * arrow keys move and select within it.
 */
@Component({
  selector: 'app-segmented-control',
  imports: [TranslocoPipe],
  template: `
    <div
      role="radiogroup"
      [attr.aria-label]="ariaLabel()"
      class="inline-flex overflow-hidden rounded-md border border-slate-700"
    >
      @for (option of options(); track option.value; let index = $index; let first = $first) {
        <button
          type="button"
          role="radio"
          [attr.aria-checked]="value() === option.value"
          [tabindex]="tabIndexFor(option)"
          [class]="
            'px-3 py-1.5 text-sm whitespace-nowrap transition ' +
            (value() === option.value
              ? 'bg-purple-600 font-medium text-white'
              : 'bg-slate-800 text-slate-300 hover:bg-slate-700') +
            (first ? '' : ' border-l border-slate-700')
          "
          (click)="value.set(option.value)"
          (keydown)="onKeydown($event, index)"
        >
          {{ option.labelKey | transloco }}
        </button>
      }
    </div>
  `,
})
export class SegmentedControl {
  readonly options = input.required<SegmentedControlOption[]>();
  readonly ariaLabel = input('');
  readonly value = model.required<string>();

  protected tabIndexFor(option: SegmentedControlOption): number {
    const options = this.options();
    const selectedExists = options.some((candidate) => candidate.value === this.value());
    const isTabStop = selectedExists ? option.value === this.value() : option === options[0];
    return isTabStop ? 0 : -1;
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
    const options = this.options();
    const next = (index + delta + options.length) % options.length;
    this.value.set(options[next].value);
    const group = (event.currentTarget as HTMLElement).closest('[role="radiogroup"]');
    group?.querySelectorAll<HTMLButtonElement>('[role="radio"]')[next]?.focus();
  }
}
