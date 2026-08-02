import { DOCUMENT } from '@angular/common';
import { Component, ElementRef, inject, signal, viewChild } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { ThemePreference, ThemeService } from '../../core/theme/theme.service';
import { Popover } from './popover';

interface ThemeOption {
  value: ThemePreference;
  labelKey: string;
  /** Decorative — the accessible name comes from the label next to it. */
  glyph: string;
}

const THEME_OPTIONS: ThemeOption[] = [
  { value: 'system', labelKey: 'theme.system', glyph: '🖥' },
  { value: 'light', labelKey: 'theme.light', glyph: '☀' },
  { value: 'dark', labelKey: 'theme.dark', glyph: '🌙' },
];

/**
 * Theme picker for the shell header, next to the language switcher — same category of control (a
 * personal display preference, not a domain action).
 *
 * A popover menu rather than a `SegmentedControl`: three segments with text labels cost more width
 * than an `h-14` header has, especially in German, and `date-range-menu.ts` already established the
 * pattern. Also deliberately not a cycling icon button — with three states, a button that advances
 * to the next one cannot announce what that next one is.
 *
 * `menuitemradio` rather than `radio`: this is a menu of mutually exclusive settings that commits on
 * activation, which is exactly what the role is for.
 */
@Component({
  selector: 'app-theme-menu',
  imports: [Popover, TranslocoPipe],
  template: `
    <div class="relative" data-popover-anchor>
      <button
        #trigger
        type="button"
        class="inline-flex h-9 w-9 items-center justify-center rounded-md text-fg-muted transition hover:text-fg"
        aria-haspopup="menu"
        [attr.aria-expanded]="isOpen()"
        [attr.aria-label]="'theme.ariaLabel' | transloco"
        [title]="'theme.label' | transloco"
        (click)="toggle()"
      >
        <span aria-hidden="true">{{ triggerGlyph() }}</span>
      </button>

      @if (isOpen()) {
        <app-popover
          align="end"
          width="w-44"
          [ariaLabel]="'theme.label' | transloco"
          (closed)="close()"
        >
          <div role="menu" class="flex flex-col gap-0.5 p-1">
            <!-- min-h-11 on touch, tighter from sm up (§10: 44 px comfort target for pointer-coarse
                 rows, 24 px is only the floor). -->
            @for (option of themeOptions; track option.value) {
              <button
                type="button"
                role="menuitemradio"
                [attr.aria-checked]="themeService.preference() === option.value"
                [class]="
                  'flex min-h-11 items-center gap-3 rounded px-3 text-left text-sm transition sm:min-h-9 ' +
                  (themeService.preference() === option.value
                    ? 'bg-accent-selected font-medium text-on-accent'
                    : 'text-fg-body hover:bg-surface-inset')
                "
                (click)="select(option.value)"
              >
                <span aria-hidden="true">{{ option.glyph }}</span>
                <span>{{ option.labelKey | transloco }}</span>
              </button>
            }
          </div>
        </app-popover>
      }
    </div>
  `,
})
export class ThemeMenu {
  protected readonly themeService = inject(ThemeService);

  private readonly document = inject(DOCUMENT);
  private readonly elementRef = inject(ElementRef<HTMLElement>);
  private readonly trigger = viewChild<ElementRef<HTMLButtonElement>>('trigger');

  protected readonly themeOptions = THEME_OPTIONS;
  protected readonly isOpen = signal(false);

  /** The trigger shows what is currently *rendered*, not what was chosen — with `'system'` selected
   *  the monitor glyph would say nothing about which mode the user is looking at. */
  protected triggerGlyph(): string {
    return this.themeService.resolved() === 'light' ? '☀' : '🌙';
  }

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
    if (hadFocus) {
      this.trigger()?.nativeElement.focus();
    }
  }

  protected select(preference: ThemePreference): void {
    this.themeService.setPreference(preference);
    this.close();
  }
}
