import { Signal, computed, inject } from '@angular/core';

import { ThemeService } from '../../core/theme/theme.service';

export type LogoVariant = 'logo' | 'logo-hero';

/**
 * The brand mark for the current mode. Two files per variant, generated together by
 * `web/branding/make-icons.ps1` and cropped against the same bounding box, so the light and dark
 * twins fill the identical share of their canvas and can be swapped on one `<img>` box without the
 * mark jumping or resizing.
 *
 * Driven by the theme signal rather than by `<picture media="(prefers-color-scheme: …)">`: a media
 * query answers what the *system* prefers, and this app lets the user overrule that. On an explicit
 * choice against the system preference the CSS route would show the wrong mark.
 *
 * Call from a field initializer — it injects.
 */
export function logoSrc(variant: LogoVariant = 'logo'): Signal<string> {
  const themeService = inject(ThemeService);
  return computed(() =>
    themeService.resolved() === 'light' ? `${variant}-light.png` : `${variant}.png`,
  );
}
