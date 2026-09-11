import { DOCUMENT } from '@angular/common';
import { TestBed } from '@angular/core/testing';
import { Subscription } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { EVENT_SOURCE_FACTORY } from './event-source.factory';
import { LiveEvent } from './live-event.model';
import { LiveUpdateService } from './live-update.service';

const READY_STATE_OPEN = 1;
const READY_STATE_CLOSED = 2;

/**
 * jsdom implements no EventSource at all, so this stands in for it through EVENT_SOURCE_FACTORY —
 * the whole reason that token exists. Only the surface LiveUpdateService touches is modelled.
 */
class FakeEventSource {
  static instances: FakeEventSource[] = [];

  readyState = READY_STATE_OPEN;
  closeCount = 0;
  onopen: (() => void) | null = null;
  onmessage: ((event: MessageEvent) => void) | null = null;
  onerror: (() => void) | null = null;

  constructor(readonly url: string) {
    FakeEventSource.instances.push(this);
  }

  close(): void {
    this.closeCount++;
    this.readyState = READY_STATE_CLOSED;
  }

  /** Delivers a raw SSE frame body, exactly as the browser would hand it over. */
  emit(data: string): void {
    this.onmessage?.({ data } as MessageEvent);
  }

  fail(readyState: number): void {
    this.readyState = readyState;
    this.onerror?.();
  }
}

/**
 * Stands in for `DOCUMENT` so tests can drive `visibilityState` and the `visibilitychange`
 * listener the service registers — jsdom's real document exposes `visibilityState` as a read-only
 * getter that cannot be reassigned from a test.
 */
class FakeDocument {
  visibilityState: 'visible' | 'hidden' = 'visible';

  private readonly listeners = new Set<() => void>();

  addEventListener(type: string, listener: () => void): void {
    if (type === 'visibilitychange') {
      this.listeners.add(listener);
    }
  }

  removeEventListener(type: string, listener: () => void): void {
    if (type === 'visibilitychange') {
      this.listeners.delete(listener);
    }
  }

  setVisibility(state: 'visible' | 'hidden'): void {
    this.visibilityState = state;
    this.listeners.forEach((listener) => listener());
  }
}

describe('LiveUpdateService', () => {
  let service: LiveUpdateService;
  let fakeDocument: FakeDocument;

  beforeEach(() => {
    FakeEventSource.instances = [];
    fakeDocument = new FakeDocument();
    vi.useFakeTimers();
    TestBed.configureTestingModule({
      providers: [
        {
          provide: EVENT_SOURCE_FACTORY,
          useValue: (url: string) => new FakeEventSource(url) as unknown as EventSource,
        },
        { provide: DOCUMENT, useValue: fakeDocument },
      ],
    });
    service = TestBed.inject(LiveUpdateService);
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  function subscribe(url = '/api/admin/live'): {
    events: LiveEvent[];
    source: FakeEventSource;
    subscription: Subscription;
  } {
    const events: LiveEvent[] = [];
    const subscription = service.stream(url).subscribe((event) => events.push(event));
    return { events, source: FakeEventSource.instances[0], subscription };
  }

  it('is cold — no connection is opened until someone subscribes', () => {
    const stream$ = service.stream('/api/admin/live');
    expect(FakeEventSource.instances).toHaveLength(0);

    const subscription = stream$.subscribe();
    expect(FakeEventSource.instances).toHaveLength(1);
    expect(FakeEventSource.instances[0].url).toBe('/api/admin/live');
    subscription.unsubscribe();
  });

  it('parses a frame and emits it to the subscriber', () => {
    const { events, source, subscription } = subscribe('/api/channels/sensitron/live');

    source.emit(JSON.stringify({ type: 'vote.changed', channel: 'sensitron', sessionId: 42 }));

    expect(events).toEqual([{ type: 'vote.changed', channel: 'sensitron', sessionId: 42 }]);
    subscription.unsubscribe();
  });

  it('swallows the ping heartbeat', () => {
    const { events, source, subscription } = subscribe();

    source.emit(JSON.stringify({ type: 'ping' }));

    expect(events).toEqual([]);
    subscription.unsubscribe();
  });

  it('drops malformed frames without killing the stream', () => {
    const { events, source, subscription } = subscribe();

    source.emit('not json at all');
    source.emit('{"channel":"sensitron"}'); // no type
    source.emit('null');
    source.emit('42');
    source.emit(JSON.stringify({ type: 'worker.health' }));

    expect(events).toEqual([{ type: 'worker.health' }]);
    subscription.unsubscribe();
  });

  it('passes unknown event types through — filtering is the consumer’s job', () => {
    const { events, source, subscription } = subscribe();

    source.emit(JSON.stringify({ type: 'something.new.from.a.future.backend' }));

    expect(events).toEqual([{ type: 'something.new.from.a.future.backend' }]);
    subscription.unsubscribe();
  });

  it('closes the connection on unsubscribe', () => {
    const { source, subscription } = subscribe();
    expect(source.closeCount).toBe(0);

    subscription.unsubscribe();

    expect(source.closeCount).toBe(1);
    expect(service.status()).toBe('idle');
  });

  it('leaves a reconnecting EventSource alone', () => {
    const { source, subscription } = subscribe();

    source.fail(0); // CONNECTING — the browser is retrying by itself

    expect(FakeEventSource.instances).toHaveLength(1);
    expect(service.status()).toBe('connecting');
    subscription.unsubscribe();
  });

  it('does not rebuild the connection immediately after a fatal error', () => {
    const { source, subscription } = subscribe();

    source.fail(READY_STATE_CLOSED); // e.g. 401 after the session was revoked

    expect(FakeEventSource.instances).toHaveLength(1);
    expect(service.status()).toBe('closed');
    subscription.unsubscribe();
  });

  it('reports open once the connection is established', () => {
    const { source, subscription } = subscribe();

    source.onopen?.();

    expect(service.status()).toBe('open');
    subscription.unsubscribe();
  });

  it('shares one connection between two subscribers of the same url', () => {
    // The workspace layout and the page routed into it both listen to this URL. Before this they
    // opened two EventSources, ran two auth handshakes and took two slots in ILiveEventStream.
    const first: LiveEvent[] = [];
    const second: LiveEvent[] = [];
    const url = '/api/channels/sensitron/live';

    const firstSubscription = service.stream(url).subscribe((event) => first.push(event));
    const secondSubscription = service.stream(url).subscribe((event) => second.push(event));

    expect(FakeEventSource.instances).toHaveLength(1);

    FakeEventSource.instances[0].emit(
      JSON.stringify({ type: 'channel.synced', channel: 'sensitron' }),
    );

    expect(first).toEqual([{ type: 'channel.synced', channel: 'sensitron' }]);
    expect(second).toEqual([{ type: 'channel.synced', channel: 'sensitron' }]);

    firstSubscription.unsubscribe();
    secondSubscription.unsubscribe();
  });

  it('keeps the connection open while one subscriber remains', () => {
    const url = '/api/channels/sensitron/live';
    const firstSubscription = service.stream(url).subscribe();
    const secondSubscription = service.stream(url).subscribe();
    const source = FakeEventSource.instances[0];

    firstSubscription.unsubscribe();

    expect(source.closeCount).toBe(0);

    secondSubscription.unsubscribe();

    expect(source.closeCount).toBe(1);
  });

  it('opens a separate connection per url', () => {
    const one = service.stream('/api/channels/one/live').subscribe();
    const two = service.stream('/api/channels/two/live').subscribe();

    expect(FakeEventSource.instances.map((instance) => instance.url)).toEqual([
      '/api/channels/one/live',
      '/api/channels/two/live',
    ]);

    one.unsubscribe();
    two.unsubscribe();
  });

  it('reconnects a url whose last subscriber had left', () => {
    // The switchMap in liveEvents() drops a channel's stream on navigation and picks it up again on
    // the way back — a cached-but-dead observable would leave that user with no live updates at all.
    const url = '/api/channels/sensitron/live';
    service.stream(url).subscribe().unsubscribe();
    expect(FakeEventSource.instances).toHaveLength(1);

    const again = service.stream(url).subscribe();

    expect(FakeEventSource.instances).toHaveLength(2);
    expect(FakeEventSource.instances[1].url).toBe(url);
    again.unsubscribe();
  });

  describe('reconnect after a fatal close (issue #128)', () => {
    it('does not reconnect before the 10 second delay elapses', () => {
      const { source, subscription } = subscribe();
      source.fail(READY_STATE_CLOSED);

      vi.advanceTimersByTime(9999);

      expect(FakeEventSource.instances).toHaveLength(1);
      subscription.unsubscribe();
    });

    it('reconnects 10 seconds after a fatal close', () => {
      const { source, subscription } = subscribe('/api/channels/sensitron/live');
      source.fail(READY_STATE_CLOSED);

      vi.advanceTimersByTime(10_000);

      expect(FakeEventSource.instances).toHaveLength(2);
      expect(FakeEventSource.instances[1].url).toBe('/api/channels/sensitron/live');
      expect(service.status()).toBe('connecting');
      subscription.unsubscribe();
    });

    it('grows the backoff exponentially and caps it at 60 seconds', () => {
      const { source, subscription } = subscribe();

      source.fail(READY_STATE_CLOSED);
      vi.advanceTimersByTime(10_000);
      expect(FakeEventSource.instances).toHaveLength(2); // 10s attempt

      FakeEventSource.instances[1].fail(READY_STATE_CLOSED);
      vi.advanceTimersByTime(19_999);
      expect(FakeEventSource.instances).toHaveLength(2); // 20s not elapsed yet
      vi.advanceTimersByTime(1);
      expect(FakeEventSource.instances).toHaveLength(3); // 20s attempt

      FakeEventSource.instances[2].fail(READY_STATE_CLOSED);
      vi.advanceTimersByTime(40_000);
      expect(FakeEventSource.instances).toHaveLength(4); // 40s attempt

      FakeEventSource.instances[3].fail(READY_STATE_CLOSED);
      vi.advanceTimersByTime(60_000);
      expect(FakeEventSource.instances).toHaveLength(5); // 60s attempt — the cap, not 80s

      FakeEventSource.instances[4].fail(READY_STATE_CLOSED);
      vi.advanceTimersByTime(60_000);
      expect(FakeEventSource.instances).toHaveLength(6); // stays at the 60s cap, does not keep growing

      subscription.unsubscribe();
    });

    it('resets the backoff to 10 seconds after a successful open', () => {
      const { source, subscription } = subscribe();

      source.fail(READY_STATE_CLOSED);
      vi.advanceTimersByTime(10_000);
      const second = FakeEventSource.instances[1];
      second.onopen?.(); // the reconnect succeeded

      second.fail(READY_STATE_CLOSED);
      vi.advanceTimersByTime(9_999);
      expect(FakeEventSource.instances).toHaveLength(2); // not yet — still short of the reset 10s
      vi.advanceTimersByTime(1);
      expect(FakeEventSource.instances).toHaveLength(3); // 10s again, not 20s — the backoff was reset

      subscription.unsubscribe();
    });

    it('clears the pending reconnect timer when the last subscriber unsubscribes', () => {
      const { source, subscription } = subscribe();
      source.fail(READY_STATE_CLOSED);

      subscription.unsubscribe();
      vi.advanceTimersByTime(60_000);

      // No reconnect may open an EventSource for a URL nobody listens to any more, and the timer
      // must not have leaked either (fake timers would otherwise still report it as pending).
      expect(FakeEventSource.instances).toHaveLength(1);
      expect(vi.getTimerCount()).toBe(0);
    });

    it('defers a reconnect while the tab is hidden and fires it once visible again', () => {
      const { source, subscription } = subscribe();
      source.fail(READY_STATE_CLOSED);
      fakeDocument.setVisibility('hidden');

      vi.advanceTimersByTime(10_000);
      expect(FakeEventSource.instances).toHaveLength(1); // backgrounded — no attempt spent on it

      fakeDocument.setVisibility('visible');
      expect(FakeEventSource.instances).toHaveLength(2); // honoured immediately, no further delay

      subscription.unsubscribe();
    });

    it('does not reconnect on a visibility change with nothing pending', () => {
      // The one reconnect path is the backoff timer; a plain tab-focus with a healthy connection
      // (or no fatal close at all) must not become a second, duplicate trigger.
      const { subscription } = subscribe();

      fakeDocument.setVisibility('hidden');
      fakeDocument.setVisibility('visible');

      expect(FakeEventSource.instances).toHaveLength(1);
      subscription.unsubscribe();
    });

    it('does not reconnect twice for one fatal close (timer and visibility never race)', () => {
      const { source, subscription } = subscribe();
      source.fail(READY_STATE_CLOSED);
      fakeDocument.setVisibility('hidden');

      vi.advanceTimersByTime(10_000); // delay elapses hidden — hands off to the visibility listener
      fakeDocument.setVisibility('visible'); // fires the deferred reconnect exactly once

      expect(FakeEventSource.instances).toHaveLength(2);
      subscription.unsubscribe();
    });
  });
});
