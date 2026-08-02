import { Component, inject } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { LanguageService, SUPPORTED_LANGS } from '../../core/i18n/language.service';

@Component({
  selector: 'app-language-switcher',
  imports: [TranslocoPipe],
  template: `
    <div class="flex items-center gap-1 text-sm">
      @for (lang of langs; track lang) {
        <button
          type="button"
          (click)="languageService.setLang(lang)"
          [attr.aria-pressed]="languageService.lang() === lang"
          [attr.aria-label]="'languageSwitcher.ariaLabel' | transloco: { lang: lang.toUpperCase() }"
          [class]="
            languageService.lang() === lang
              ? 'rounded px-1.5 py-0.5 font-semibold uppercase text-slate-100'
              : 'rounded px-1.5 py-0.5 uppercase text-slate-400 transition hover:text-slate-100'
          "
        >
          {{ lang }}
        </button>
      }
    </div>
  `,
})
export class LanguageSwitcher {
  protected readonly languageService = inject(LanguageService);
  protected readonly langs = SUPPORTED_LANGS;
}
