import { Component, input } from '@angular/core';

/**
 * A deliberate empty state instead of a bare gray sentence: says why there is nothing here and,
 * via the projected content, what to do next (CTA button/link) — NN/g's "teachable moment".
 */
@Component({
  selector: 'app-empty-state',
  template: `
    <div
      class="flex flex-col items-center gap-2 rounded-lg border border-dashed border-slate-800 px-6 py-10 text-center"
    >
      @if (icon(); as emoji) {
        <!-- Decorative emoji in a tinted square, echoing the landing's feature-card icons. -->
        <span
          class="mb-1 flex h-11 w-11 items-center justify-center rounded-md bg-purple-950 text-xl"
          aria-hidden="true"
        >
          {{ emoji }}
        </span>
      }
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
  readonly icon = input<string | null>(null);
}
