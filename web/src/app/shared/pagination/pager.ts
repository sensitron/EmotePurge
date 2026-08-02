import { Component, input, output } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { Button } from '../ui/button';

/**
 * Page switcher for the `PagedResult<T>` lists (design doc §8.4). Hides itself at a single page.
 *
 * `scrollTarget` is what keeps a page change from being a silent content swap. The pager sits below
 * its list, so the click always happens at the bottom of the document — and without repositioning,
 * the next page's rows arrive entirely *above* the viewport while the reader stays staring at the
 * end of a list they never scrolled through.
 *
 * The reposition runs **before** the `pageChange` emit, and that order is load-bearing: the emit
 * sets the page signal, the resource drops to its loading state, and the list plus this component
 * are replaced by a skeleton. Anything done afterwards would run against a detached element — and
 * a keyboard user's focus would already have fallen back to `<body>`.
 */
@Component({
  selector: 'app-pager',
  imports: [Button, TranslocoPipe],
  template: `
    @if (totalPages() > 1) {
      <nav
        class="flex items-center justify-between text-sm text-fg-muted"
        [attr.aria-label]="'pager.label' | transloco"
      >
        <button
          type="button"
          appButton="outline"
          class="disabled:hover:bg-transparent"
          [disabled]="page() <= 1"
          (click)="goToPage(page() - 1)"
        >
          {{ 'pager.previous' | transloco }}
        </button>
        <!-- Live region: moving focus to the results announces *what* is on screen, never which
             page it is — this line is the only thing that says the switch happened at all. -->
        <span role="status">
          {{ 'pager.pageOf' | transloco: { page: page(), totalPages: totalPages() } }}
        </span>
        <button
          type="button"
          appButton="outline"
          class="disabled:hover:bg-transparent"
          [disabled]="page() >= totalPages()"
          (click)="goToPage(page() + 1)"
        >
          {{ 'pager.next' | transloco }}
        </button>
      </nav>
    }
  `,
})
export class Pager {
  readonly page = input.required<number>();
  readonly totalPages = input.required<number>();
  /**
   * Where the viewport goes after a page change: the top of the results region, passed as a template
   * reference. The target carries two things of its own — `tabindex="-1"` so it can take focus
   * without becoming a tab stop, and a `scroll-mt-*` matching the sticky layers stacked above it
   * (§8.5), because `scrollIntoView` would otherwise park it behind the header.
   *
   * Optional, so a short list that never leaves the viewport can stay as it is.
   */
  readonly scrollTarget = input<HTMLElement>();
  readonly pageChange = output<number>();

  protected goToPage(target: number): void {
    this.repositionViewport();
    this.pageChange.emit(target);
  }

  private repositionViewport(): void {
    const anchor = this.scrollTarget();
    if (!anchor) {
      return;
    }
    // Focus and scroll are two separate jobs here. `focus()` scrolls as a side effect, but only when
    // the element is off screen and only to "nearest" — the reposition has to be unconditional and
    // to the top, so it gets its own call and the focus is told to keep its hands off.
    anchor.focus({ preventScroll: true });
    // Deliberately no `behavior: 'smooth'`: this is a content replacement, not a guided tour, and
    // animating several screen heights is both slower than the reader and a motion trigger.
    anchor.scrollIntoView();
  }
}
