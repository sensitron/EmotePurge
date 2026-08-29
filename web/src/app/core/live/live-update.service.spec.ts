import { TestBed } from '@angular/core/testing';
import { Subscription } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';

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

describe('LiveUpdateService', () => {
  let service: LiveUpdateService;

  beforeEach(() => {
    FakeEventSource.instances = [];
    TestBed.configureTestingModule({
      providers: [
        {
          provide: EVENT_SOURCE_FACTORY,
          useValue: (url: string) => new FakeEventSource(url) as unknown as EventSource,
        },
      ],
    });
    service = TestBed.inject(LiveUpdateService);
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

  it('does not rebuild the connection after a fatal error', () => {
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
});
