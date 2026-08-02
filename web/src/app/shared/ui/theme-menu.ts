import { DOCUMENT } from '@angular/common';
import { Component, ElementRef, inject, input, signal, viewChild } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { ThemePreference, ThemeService } from '../../core/theme/theme.service';
import { Popover } from './popover';

interface ThemeOption {
  value: ThemePreference;
  labelKey: string;
}

const THEME_OPTIONS: ThemeOption[] = [
  { value: 'system', labelKey: 'theme.system' },
  { value: 'light', labelKey: 'theme.light' },
  { value: 'dark', labelKey: 'theme.dark' },
];

/**
 * The three mode glyphs as drawn icons rather than emoji.
 *
 * Emoji are a font, not artwork: `☀` and `🌙` render as a different picture on every platform, one
 * of the two is usually presented in colour and the other monochrome, and neither takes
 * `currentColor` — so the selected row, whose text turns white on the accent fill, kept a glyph that
 * did not follow it. These are 24-unit outline paths at the same stroke weight as the shell's own
 * icons (`app-shell.ts`), so they inherit colour, size and weight like text does.
 *
 * `name` takes a `ThemePreference` because the trigger passes a *resolved* theme (`'light'|'dark'`),
 * which is a subset of it — one type, no mapping table.
 */
@Component({
  selector: 'app-theme-icon',
  template: `
    <svg
      class="h-5 w-5 shrink-0"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      stroke-width="1.75"
      stroke-linecap="round"
      stroke-linejoin="round"
      aria-hidden="true"
    >
      @switch (name()) {
        @case ('light') {
          <circle cx="12" cy="12" r="4" />
          <path
            d="M12 2.75v2M12 19.25v2M21.25 12h-2M4.75 12h-2M18.54 5.46l-1.42 1.42M6.88 17.12l-1.42 1.42M18.54 18.54l-1.42-1.42M6.88 6.88L5.46 5.46"
          />
        }
        @case ('dark') {
          <!-- Crescent as a single outline: a full disc minus the disc that bites into it. -->
          <path d="M20.5 14.4A8.6 8.6 0 0 1 9.6 3.5a8.6 8.6 0 1 0 10.9 10.9z" />
        }
        @default {
          <!-- 'system' = a display, i.e. "whatever the device says". -->
          <rect x="2.75" y="4" width="18.5" height="12.5" rx="2" />
          <path d="M8.5 20.25h7M12 16.5v3.75" />
        }
      }
    </svg>
  `,
})
export class ThemeIcon {
  readonly name = input.required<ThemePreference>();
}

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
  imports: [Popover, ThemeIcon, TranslocoPipe],
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
        <!-- resolved(), not preference(): the trigger shows what is currently on screen. With
             'system' chosen, a display icon here would say nothing about which mode you are in. -->
        <app-theme-icon [name]="themeService.resolved()" />
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
                <app-theme-icon [name]="option.value" />
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
