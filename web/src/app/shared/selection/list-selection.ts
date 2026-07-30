import { computed, signal } from '@angular/core';

/**
 * Checkbox + shift-click range multi-select over a (possibly virtual-scrolled) list.
 * Not a service — selection is page-local UI state, like a FormControl, not app-wide state.
 * `items` must return the full logically-ordered/filtered list (not the DOM-rendered subset),
 * so the shift-click range stays correct regardless of what CdkVirtualScrollViewport has mounted.
 *
 * Keyed, never identity- or position-based: the selection stores `keyFn(item)` (the emote id).
 * A refetch hands out freshly deserialized objects for the very same rows, and flipping the sort
 * direction moves every row to a different position — both used to silently desynchronize the
 * rendered checkboxes from what the delete path actually submitted.
 *
 * Two deliberately separate views on the selection:
 *  - `selectedKeys` is authoritative. It does not depend on the item still being present in
 *    `items()`, so it stays complete across refetch, re-sort and filtering. Anything that must
 *    not miss a selected entry (counting, deciding what gets deleted) belongs here.
 *  - `selectedItems` resolves those keys back against the current `items()`, for consumers that
 *    need more than the key (preview names in the delete confirmation). It can only ever return
 *    rows that are currently visible, which makes it a display convenience, not the source of truth.
 *
 * Backed by a signal (not @angular/cdk/collections' SelectionModel) — a computed() elsewhere that
 * reads the selection needs an actual signal read to know when to recompute; a plain mutable
 * SelectionModel gives it nothing to track, so toggling a checkbox would never trigger a re-render.
 */
export class ListSelection<T> {
  private readonly selectedKeySet = signal<ReadonlySet<string>>(new Set());

  // The shift-click anchor is the anchored row's key, resolved against the *current* items() at
  // click time. Stored as a position index it silently pointed at a different row after every
  // re-sort, which turned a shift-click into a range over rows the user never saw selected.
  private anchorKey: string | null = null;

  readonly selectedKeys = computed(() => Array.from(this.selectedKeySet()));

  readonly selectedItems = computed<T[]>(() => {
    const keys = this.selectedKeySet();
    return this.items().filter((item) => keys.has(this.keyFn(item)));
  });

  constructor(
    private readonly items: () => readonly T[],
    private readonly keyFn: (item: T) => string,
  ) {}

  isSelected(item: T): boolean {
    return this.selectedKeySet().has(this.keyFn(item));
  }

  onRowClick(item: T, event: MouseEvent): void {
    const items = this.items();
    const key = this.keyFn(item);
    const clickedIndex = items.findIndex((candidate) => this.keyFn(candidate) === key);
    const anchorKey = this.anchorKey;
    const anchorIndex = anchorKey === null ? -1 : items.findIndex((candidate) => this.keyFn(candidate) === anchorKey);

    const next = new Set(this.selectedKeySet());

    // An anchor that is no longer visible (filtered out, deleted, replaced by another channel's
    // data) has no meaningful range to the clicked row — degrade to a single toggle instead of
    // guessing, since the wrong guess ends in irreversibly deleted emotes.
    if (event.shiftKey && anchorIndex !== -1 && clickedIndex !== -1) {
      const [start, end] = anchorIndex < clickedIndex ? [anchorIndex, clickedIndex] : [clickedIndex, anchorIndex];
      for (const ranged of items.slice(start, end + 1)) {
        next.add(this.keyFn(ranged));
      }
    } else if (next.has(key)) {
      next.delete(key);
    } else {
      next.add(key);
    }

    this.selectedKeySet.set(next);
    this.anchorKey = key;
  }

  clear(): void {
    this.selectedKeySet.set(new Set());
    this.anchorKey = null;
  }

  // Filter changes prune instead of clearing (S2-16): what stays visible stays selected, while a
  // key that is filtered out of items() must not linger — selectedKeys is authoritative for the
  // delete path, and an invisible-but-selected emote would be deleted without being on screen.
  retainVisible(): void {
    const visible = new Set(this.items().map((item) => this.keyFn(item)));
    this.selectedKeySet.update((keys) => new Set([...keys].filter((key) => visible.has(key))));
    if (this.anchorKey !== null && !visible.has(this.anchorKey)) {
      this.anchorKey = null;
    }
  }
}
