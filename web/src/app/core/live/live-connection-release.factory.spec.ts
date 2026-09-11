import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  LIVE_CONNECTION_RELEASE_FACTORY,
  LiveConnectionReleaseFactory,
  liveConnectionReleaseUrl,
} from './live-connection-release.factory';

describe('LIVE_CONNECTION_RELEASE_FACTORY (issue #128 follow-up)', () => {
  let fetchSpy: ReturnType<typeof vi.fn>;
  let release: LiveConnectionReleaseFactory;

  beforeEach(() => {
    fetchSpy = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal('fetch', fetchSpy);
    TestBed.configureTestingModule({});
    release = TestBed.inject(LIVE_CONNECTION_RELEASE_FACTORY);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('DELETEs the connection with keepalive and same-origin credentials', () => {
    release('a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4');

    expect(fetchSpy).toHaveBeenCalledExactlyOnceWith(
      liveConnectionReleaseUrl('a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4'),
      { method: 'DELETE', keepalive: true, credentials: 'same-origin' },
    );
  });

  it('builds the url from the connection id, nothing else', () => {
    expect(liveConnectionReleaseUrl('deadbeef')).toBe('/api/live/connections/deadbeef');
  });

  it('swallows a rejected release fetch — nothing to react to on this fire-and-forget call', async () => {
    fetchSpy.mockRejectedValue(new Error('network down'));

    expect(() => release('a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4')).not.toThrow();

    // Flushes the microtask queue so the rejected fetch's `.catch()` actually runs before the test
    // ends. Vitest fails a test on an unhandled rejection, so a regression that forgot to chain the
    // `.catch()` in the factory would surface here rather than passing silently.
    await Promise.resolve();
    await Promise.resolve();
  });
});
