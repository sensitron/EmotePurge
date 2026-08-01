import { describe, expect, it } from 'vitest';

import { SUPPORTED_LANGS } from './language.service';
import { toLocale } from './locale';

// The point of this mapping is that Angular's LOCALE_ID is bootstrap-time static and cannot follow
// a runtime language switch — so every live-updating date and number goes through here instead.
describe('toLocale', () => {
  it('maps the supported languages to BCP-47 tags', () => {
    expect(toLocale('de')).toBe('de-DE');
    expect(toLocale('en')).toBe('en-US');
  });

  it('covers every supported language', () => {
    // Guards the real failure mode: adding a language to SUPPORTED_LANGS without extending
    // LOCALE_TAGS yields undefined here, and Intl then silently falls back to the host locale.
    for (const lang of SUPPORTED_LANGS) {
      expect(toLocale(lang)).toBeTruthy();
    }
  });

  it('produces tags Intl actually accepts', () => {
    for (const lang of SUPPORTED_LANGS) {
      expect(() => new Intl.NumberFormat(toLocale(lang))).not.toThrow();
      expect(Intl.NumberFormat.supportedLocalesOf(toLocale(lang))).toHaveLength(1);
    }
  });

  it('formats numbers differently per language — the reason this exists at all', () => {
    expect(new Intl.NumberFormat(toLocale('de')).format(12.5)).toBe('12,5');
    expect(new Intl.NumberFormat(toLocale('en')).format(12.5)).toBe('12.5');
  });
});
