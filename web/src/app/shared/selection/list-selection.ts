import { computed, signal } from '@angular/core';

/**
 * Checkbox + shift-click range multi-select over a (possibly virtual-scrolled) list.
 * Not a service — selection is page-local UI state, like a FormControl, not app-wide state.
 * `items` must return the full logically-ordered/filtered list (not the DOM-rendered subset),
 * so the shift-click range stays correct regardless of what CdkVirtualScrollViewport has mounted.
 *
 * Backed by a signal (not @angular/cdk/collections' SelectionModel) — a computed() elsewhere that
 * reads `.selected()` needs an actual signal read to know when to recompute; a plain mutable
 * SelectionModel gives it nothing to track, so toggling a checkbox would never trigger a re-render.
 */
export class ListSelection<T> {
  private readonly selectedSet = signal<ReadonlySet<T>>(new Set());
  private lastIndex: number | null = null;

  readonly selected = computed(() => Array.from(this.selectedSet()));

  constructor(private readonly items: () => readonly T[]) {}

  isSelected(item: T): boolean {
    return this.selectedSet().has(item);
  }

  onRowClick(item: T, index: number, event: MouseEvent): void {
    const next = new Set(this.selectedSet());

    if (event.shiftKey && this.lastIndex !== null) {
      const [start, end] = this.lastIndex < index ? [this.lastIndex, index] : [index, this.lastIndex];
      this.items()
        .slice(start, end + 1)
        .forEach((selected) => next.add(selected));
    } else if (next.has(item)) {
      next.delete(item);
    } else {
      next.add(item);
    }

    this.selectedSet.set(next);
    this.lastIndex = index;
  }

  clear(): void {
    this.selectedSet.set(new Set());
    this.lastIndex = null;
  }
}
