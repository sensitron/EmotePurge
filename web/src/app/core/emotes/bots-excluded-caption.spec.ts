import { describe, expect, it } from 'vitest';

import { botsExcludedCaptionKey } from './bots-excluded-caption';

describe('botsExcludedCaptionKey', () => {
  it('returns null when no bot has ever been seen', () => {
    expect(botsExcludedCaptionKey(null)).toBeNull();
  });

  it('returns the caption key when a date is present', () => {
    expect(botsExcludedCaptionKey('2026-09-01')).toBe('usageStats.botsExcludedSince');
  });
});
