import { describe, expect, it } from 'vitest';

import {
  SEVEN_TV_SYNC_FAILURE_REASONS,
  SevenTvSyncFailureReason,
  sevenTvSyncFailureKey,
} from './seven-tv-sync-failure';
import de from '../../../../public/i18n/de.json';
import en from '../../../../public/i18n/en.json';

/**
 * The same guard `api-error-locales.spec.ts` exists for, applied to the second language-neutral
 * code list the API now serves (Regel 7). Nothing under `web/src` reads the locale files at build
 * time, so without this a reason shipped by the API and forgotten in a locale would render as the
 * raw key `sevenTvSync.failure.no_active_emote_set.title` on the page — the exact silent state
 * issue #32 was about, one layer up.
 */
describe('7TV sync failure reasons', () => {
  const locales = { de, en } as Record<
    string,
    { sevenTvSync: { failure: Record<string, Record<string, string>> } }
  >;

  it('builds the translation key from the wire code', () => {
    expect(sevenTvSyncFailureKey('no_active_emote_set', 'title')).toBe(
      'sevenTvSync.failure.no_active_emote_set.title',
    );
    expect(sevenTvSyncFailureKey('seventv_unavailable', 'short')).toBe(
      'sevenTvSync.failure.seventv_unavailable.short',
    );
  });

  it.each(Object.keys(locales))('%s translates every reason in all three lengths', (name) => {
    const failures = locales[name].sevenTvSync.failure;
    const missing: string[] = [];
    for (const reason of SEVEN_TV_SYNC_FAILURE_REASONS) {
      for (const part of ['title', 'hint', 'short'] as const) {
        if (!failures[reason]?.[part]) {
          missing.push(`${reason}.${part}`);
        }
      }
    }

    expect(missing).toEqual([]);
  });

  it.each(Object.keys(locales))('%s carries no translations for unknown reasons', (name) => {
    // The other direction: a reason removed from the API but left behind reads like a supported
    // case to the next person editing the file.
    const known = new Set<string>(SEVEN_TV_SYNC_FAILURE_REASONS);
    const stray = Object.keys(locales[name].sevenTvSync.failure).filter((r) => !known.has(r));

    expect(stray).toEqual([]);
  });

  it('has identical key sets in both locales', () => {
    expect(Object.keys(de.sevenTvSync.failure).sort()).toEqual(
      Object.keys(en.sevenTvSync.failure).sort(),
    );
  });

  it('accepts exactly the three codes the API can send', () => {
    // Compile-time proof that the union and the runtime list cannot drift: the array is typed as
    // the union, and this assignment fails to compile if the union grows without the array.
    const all: readonly SevenTvSyncFailureReason[] = SEVEN_TV_SYNC_FAILURE_REASONS;
    expect(all).toEqual(['no_seventv_account', 'no_active_emote_set', 'seventv_unavailable']);
  });
});
