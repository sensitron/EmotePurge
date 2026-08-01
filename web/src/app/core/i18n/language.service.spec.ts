import { TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { LanguageService, resolveInitialLang } from './language.service';

function setBrowserLanguage(value: string): void {
  vi.spyOn(navigator, 'language', 'get').mockReturnValue(value);
}

describe('resolveInitialLang', () => {
  beforeEach(() => localStorage.clear());
  afterEach(() => vi.restoreAllMocks());

  it('prefers the stored preference over the browser language', () => {
    localStorage.setItem('ep_lang', 'en');
    setBrowserLanguage('de-DE');

    expect(resolveInitialLang()).toBe('en');
  });

  it('falls back to the browser language when nothing is stored', () => {
    setBrowserLanguage('en-GB');

    expect(resolveInitialLang()).toBe('en');
  });

  it('falls back to German for an unsupported browser language', () => {
    // German, not English: the primary audience is a German-speaking Twitch community.
    setBrowserLanguage('fr-FR');

    expect(resolveInitialLang()).toBe('de');
  });

  it('ignores a stored value that is not a supported language', () => {
    // Anyone can hand-edit localStorage; a junk value must not become the active language.
    localStorage.setItem('ep_lang', 'klingon');
    setBrowserLanguage('en-US');

    expect(resolveInitialLang()).toBe('en');
  });
});

describe('LanguageService', () => {
  let service: LanguageService;
  let transloco: TranslocoService;

  beforeEach(() => {
    localStorage.clear();
    setBrowserLanguage('de-DE');
    TestBed.configureTestingModule({
      imports: [
        TranslocoTestingModule.forRoot({
          langs: { de: {}, en: {} },
          translocoConfig: { availableLangs: ['de', 'en'], defaultLang: 'de' },
        }),
      ],
    });
    service = TestBed.inject(LanguageService);
    transloco = TestBed.inject(TranslocoService);
  });

  afterEach(() => vi.restoreAllMocks());

  it('sets the document language on construction', () => {
    expect(document.documentElement.lang).toBe('de');
  });

  it('setLang updates the signal, Transloco, the document and localStorage together', () => {
    service.setLang('en');

    expect(service.lang()).toBe('en');
    expect(transloco.getActiveLang()).toBe('en');
    // <html lang> is what screen readers switch pronunciation on — it must not drift from the
    // language actually being rendered.
    expect(document.documentElement.lang).toBe('en');
    expect(localStorage.getItem('ep_lang')).toBe('en');
  });

  it('persists the choice so the next visit skips the browser-language guess', () => {
    service.setLang('en');

    expect(resolveInitialLang()).toBe('en');
  });
});
