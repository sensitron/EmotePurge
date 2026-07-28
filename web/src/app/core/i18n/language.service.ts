import { inject, Injectable, signal } from '@angular/core';
import { TranslocoService } from '@jsverse/transloco';

export const SUPPORTED_LANGS = ['de', 'en'] as const;
export type AppLang = (typeof SUPPORTED_LANGS)[number];

const LANG_STORAGE_KEY = 'ep_lang';

function isAppLang(value: string | null): value is AppLang {
  return (SUPPORTED_LANGS as readonly string[]).includes(value ?? '');
}

/** Stored preference wins; otherwise the browser's language; otherwise German (primary audience). */
export function resolveInitialLang(): AppLang {
  const stored = localStorage.getItem(LANG_STORAGE_KEY);
  if (isAppLang(stored)) {
    return stored;
  }
  const browserLang = navigator.language.slice(0, 2).toLowerCase();
  return isAppLang(browserLang) ? browserLang : 'de';
}

@Injectable({ providedIn: 'root' })
export class LanguageService {
  private readonly translocoService = inject(TranslocoService);

  readonly lang = signal<AppLang>(resolveInitialLang());

  constructor() {
    document.documentElement.lang = this.lang();
  }

  setLang(lang: AppLang): void {
    this.lang.set(lang);
    this.translocoService.setActiveLang(lang);
    document.documentElement.lang = lang;
    localStorage.setItem(LANG_STORAGE_KEY, lang);
  }
}
