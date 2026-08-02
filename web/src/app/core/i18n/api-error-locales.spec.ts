import { describe, expect, it } from 'vitest';

import { KNOWN_API_ERROR_CODES } from './api-error';
import de from '../../../../public/i18n/de.json';
import en from '../../../../public/i18n/en.json';

/**
 * Closes a gap the project rule assumed was already covered: nothing under `web/src` reads the
 * locale files, so `api-error.spec.ts` only ever tested the mapping function. The proof it was
 * needed is in the repo's own history — `no_health_data` and `health_data_unreadable` shipped in
 * `ApiErrorCodes.cs` and existed in neither `KNOWN_API_ERROR_CODES` nor either locale, so the API
 * returned a code the UI silently downgraded to a generic status message.
 *
 * What this cannot see is the C# side: a code added to `ApiErrorCodes.cs` and nowhere else still
 * drifts unnoticed. That would need the test to parse a .cs file, which is a worse trade than
 * catching the two-thirds of the mirror that live here.
 */
describe('api error locales', () => {
  const locales = { de, en } as Record<string, { errors: { api: Record<string, string> } }>;

  it.each(Object.keys(locales))('%s translates every known API error code', (name) => {
    const translations = locales[name].errors.api;
    const missing = [...KNOWN_API_ERROR_CODES].filter((code) => !(code in translations));

    expect(missing).toEqual([]);
  });

  it.each(Object.keys(locales))('%s carries no translations for unknown codes', (name) => {
    // The other direction: a code removed from the API but left in the locale file is dead weight
    // that reads like a supported case.
    const stray = Object.keys(locales[name].errors.api).filter(
      (code) => !KNOWN_API_ERROR_CODES.has(code),
    );

    expect(stray).toEqual([]);
  });

  it('has identical key sets in both locales', () => {
    expect(Object.keys(de.errors.api).sort()).toEqual(Object.keys(en.errors.api).sort());
  });
});
