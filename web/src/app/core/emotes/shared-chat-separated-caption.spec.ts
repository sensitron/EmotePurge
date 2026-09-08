import { describe, expect, it } from 'vitest';

import { sharedChatSeparatedCaptionKey } from './shared-chat-separated-caption';

describe('sharedChatSeparatedCaptionKey', () => {
  it('returns null when no shared chat has ever been seen', () => {
    expect(sharedChatSeparatedCaptionKey(null)).toBeNull();
  });

  it('returns the caption key when a date is present', () => {
    expect(sharedChatSeparatedCaptionKey('2026-09-07')).toBe('usageStats.sharedChatSeparatedSince');
  });
});
