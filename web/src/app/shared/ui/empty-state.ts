import { Component, input } from '@angular/core';

/**
 * A deliberate empty state instead of a bare gray sentence: says why there is nothing here and,
 * via the projected content, what to do next (CTA button/link) — NN/g's "teachable moment".
 */
@Component({
  selector: 'app-empty-state',
  template: `
    <div class="flex flex-col items-center gap-2 rounded-md border border-dashed border-slate-800 px-6 py-10 text-center">
      <p class="text-sm font-medium text-slate-300">{{ title() }}</p>
      @if (description(); as text) {
        <p class="max-w-md text-sm text-slate-400">{{ text }}</p>
      }
      <div class="mt-2 empty:hidden"><ng-content /></div>
    </div>
  `,
})
export class EmptyState {
  readonly title = input.required<string>();
  readonly description = input<string | null>(null);
}
