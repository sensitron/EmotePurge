import { Component, computed, input } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { pluralKey } from '../../core/i18n/plural';

/** Beyond this the list stops being readable and starts being a wall — the tail is counted instead. */
const PREVIEW_CAP = 50;

/**
 * The list of names a dialog is about to act on (delete, restore).
 *
 * Both confirm dialogs had built this themselves, down to the same cap, the same overflow line and
 * the same translation key — but not the same data contract, so neither could use the other's.
 *
 * Ruled rows rather than the old `space-y-1`: this is the one list inside the dialog family, and the
 * design language's list recipe (§2.1) is what makes fifty names scannable. Full-bleed (`-mx-6`
 * against the shell's `p-6`) so the rules read as bands across the dialog, not as a boxed table.
 */
@Component({
  selector: 'app-name-preview-list',
  imports: [TranslocoPipe],
  template: `
    <ul
      class="-mx-6 max-h-48 divide-y divide-border overflow-y-auto border-y border-border text-sm"
    >
      @for (name of preview(); track name) {
        <li class="px-6 py-1.5 text-fg-secondary">{{ name }}</li>
      }
      @if (remaining() > 0) {
        <li class="px-6 py-1.5 text-fg-muted">
          {{ andMoreKey() | transloco: { count: remaining() } }}
        </li>
      }
    </ul>
  `,
})
export class NamePreviewList {
  readonly names = input.required<readonly string[]>();

  protected readonly preview = computed(() => this.names().slice(0, PREVIEW_CAP));
  protected readonly remaining = computed(() => this.names().length - this.preview().length);
  protected readonly andMoreKey = computed(() => pluralKey(this.remaining(), 'common.andMore'));
}
