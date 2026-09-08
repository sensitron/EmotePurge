/**
 * The first spec in this repo that mounts a routed *page* component rather than a service, guard,
 * dialog or a shared/ presentational piece — no established pattern existed for this (see the
 * feasibility spike this file grew out of), so a few choices here are worth spelling out for
 * whoever touches `UsageStatsPage` next.
 *
 * **The template is replaced, not the logic.** `TestBed.overrideComponent` swaps the real 825-line
 * template — the whole atlas/sidecar/dock component graph, each with its own dependencies — for two
 * bare `<div>`s. That is not disabling behaviour under test: it exists purely so the constructor's
 * `viewChild.required<ElementRef>('sheet')`/`('stickyBar')` reads succeed and their `ResizeObserver`
 * effects can attach to *something*, instead of throwing `NG0951` for a ref the real template would
 * only ever satisfy through components this test has no reason to construct. Every signal, computed,
 * effect and HTTP call in the class itself runs unmodified.
 *
 * **The HTTP round sequence and the two `fixture.detectChanges()` calls are load()'s choreography,
 * not this spec's.** `load()` fires `getSetStatus` unconditionally, then only fires `getTotals`/
 * `getChannelSeries` once `rangeResolved` is true — which for the "all time" default requires a
 * *second* effect run after the tracked-since date corrects `from()` (see that effect's own comment
 * in usage-stats-page.ts). This spec has to flush requests and tick change detection in the same
 * order and count `load()` actually produces. A refactor of `load()`, the "all time" effect, or
 * `refreshSetStatus()` itself will very likely change how many HTTP round trips happen and in what
 * order — when that happens, adjust the flushing choreography below to match the new sequence, not
 * the assertions at the end of the test, which are the actual thing under test.
 *
 * There are three cases below, not one. The first two cover the discard branch — A's stale answer
 * can land in either of two windows, before B's own load has finished or after, and only the
 * second is the shape #112 actually reported: it is the one where `setStatusChannel` has already
 * been bumped to `'b'` by B's own success before A's answer arrives, which is what let the pre-fix
 * code's unconditional `setStatus.set(status)` slip A's set id in under a
 * `setStatusChannel`/`importScopeCurrent` that still read as correct. The third case covers the
 * other branch of the same `if` — the successful silent refresh that *does* get to claim the
 * channel, which is the other half of the fix (`setStatusChannel` being written at all, not just
 * being guarded).
 */
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { channelLiveUrl, LIVE_EVENT_TYPES } from '../../core/live/live-event.model';
import { CHANNEL_RELOAD_DEBOUNCE_MS } from '../../core/live/live-reload';
import { EVENT_SOURCE_FACTORY } from '../../core/live/event-source.factory';
import { EmoteSetStatus } from '../../core/emotes/emote-set-status.model';
import { EmoteUsageTotal } from '../../core/usage-stats/usage-stat.model';
import { UsageStatsPage } from './usage-stats-page';

/** Same stand-in as core/live/live-reload.spec.ts — jsdom ships no EventSource at all. */
class FakeEventSource {
  static instances: FakeEventSource[] = [];

  onmessage: ((event: MessageEvent) => void) | null = null;
  onopen: (() => void) | null = null;
  onerror: (() => void) | null = null;
  closeCount = 0;

  constructor(readonly url: string) {
    FakeEventSource.instances.push(this);
  }

  close(): void {
    this.closeCount++;
  }

  emit(event: { type: string; channel?: string }): void {
    this.onmessage?.({ data: JSON.stringify(event) } as MessageEvent);
  }
}

/** jsdom implements no ResizeObserver either — two of the constructor's effects touch it. */
class FakeResizeObserver {
  observe(): void {
    /* no-op */
  }
  unobserve(): void {
    /* no-op */
  }
  disconnect(): void {
    /* no-op */
  }
}

function setStatus(overrides: Partial<EmoteSetStatus>): EmoteSetStatus {
  return {
    activeEmoteSetId: 'set-a',
    capacity: 600,
    occupiedSlots: 10,
    trackedSince: '2026-01-01T00:00:00Z',
    syncFailureReason: null,
    lastSyncAttemptAtUtc: null,
    botsExcludedSince: null,
    sharedChatSeparatedSince: null,
    ...overrides,
  };
}

function emote(id: string, name: string, totalUseCount = 10): EmoteUsageTotal {
  return {
    emoteId: id,
    emoteName: name,
    sevenTvEmoteId: `7tv-${id}`,
    imageUrl: '',
    totalUseCount,
    lastUsedDate: null,
    previousWindowUseCount: 0,
    firstSeenAt: null,
  };
}

/**
 * `HttpTestingController.match('/some/path')` compares the string against `urlWithParams` — so it
 * silently matches nothing (and flushes nothing) for `getTotals`/`getChannelSeries`, which both
 * carry a `?from=&to=` query string. This matches on the path alone, the way every call site below
 * actually means it.
 */
function flushByPath(mock: HttpTestingController, path: string, body: object | unknown[]): void {
  mock.match((req) => req.url === path).forEach((testReq) => testReq.flush(body));
}

describe('UsageStatsPage — refreshSetStatus channel race (#112 regression)', () => {
  let fixture: ComponentFixture<UsageStatsPage>;
  let component: UsageStatsPage;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    FakeEventSource.instances = [];
    vi.stubGlobal('ResizeObserver', FakeResizeObserver);
    vi.useFakeTimers();

    TestBed.configureTestingModule({
      imports: [
        TranslocoTestingModule.forRoot({
          langs: { de: {} },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          provide: EVENT_SOURCE_FACTORY,
          useValue: (url: string) => new FakeEventSource(url) as unknown as EventSource,
        },
      ],
    });

    // The real 825-line template pulls in the whole atlas/sidecar/dock component graph. Nothing
    // under test here reads from the DOM — only the two `viewChild.required` refs the constructor's
    // ResizeObserver effects need to resolve without throwing NG0951.
    TestBed.overrideComponent(UsageStatsPage, {
      set: { template: '<div #sheet></div><div #stickyBar></div>' },
    });

    fixture = TestBed.createComponent(UsageStatsPage);
    component = fixture.componentInstance;
    httpMock = TestBed.inject(HttpTestingController);

    fixture.componentRef.setInput('channelName', 'a');
    fixture.detectChanges();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it("discards A's late refreshSetStatus() answer arriving before B's own load has finished", () => {
    // --- Mount on channel A: permissions + the initial getSetStatus + totals/series once the
    // "all time" range resolves against A's trackedSince. ---
    httpMock
      .expectOne('/api/channels/a/permissions')
      .flush({ canManage: true, canViewUsageStats: true });

    httpMock
      .expectOne('/api/channels/a/emotes/active-set')
      .flush(setStatus({ activeEmoteSetId: 'set-a', trackedSince: '2026-01-01T00:00:00Z' }));
    // The trackedSince above corrects from(), which reruns the load effect and fires totals/series.
    fixture.detectChanges();

    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', []);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', {
      from: '2026-01-01',
      to: '2026-09-08',
      liveDays: [],
      emotes: [],
    });

    expect(component['setStatusChannel']()).toBe('a');

    // --- A channel.synced burst on A's live stream fires refreshSetStatus() for A. Leave that
    // request pending — this is the in-flight request the switch below must outrun. ---
    expect(FakeEventSource.instances).toHaveLength(1);
    const sourceA = FakeEventSource.instances[0];
    expect(sourceA.url).toBe(channelLiveUrl('a'));

    sourceA.emit({ type: LIVE_EVENT_TYPES.channelSynced, channel: 'a' });
    vi.advanceTimersByTime(1000); // CHANNEL_RELOAD_DEBOUNCE_MS
    fixture.detectChanges();

    // The liveReload burst also re-triggers a quiet totals reload for A — drain it, it is not part
    // of what this test is pinning down.
    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', []);

    const staleStatusReq = httpMock.expectOne('/api/channels/a/emotes/active-set');

    // --- Switch channels within the same route before A's refresh answers. ---
    fixture.componentRef.setInput('channelName', 'b');
    fixture.detectChanges();

    // The switch closes A's live connection and opens B's.
    expect(sourceA.closeCount).toBe(1);
    expect(FakeEventSource.instances).toHaveLength(2);
    expect(FakeEventSource.instances[1].url).toBe(channelLiveUrl('b'));

    // load() fires B's own getSetStatus immediately (rangeResolved is false for the new channel).
    const freshStatusReqB = httpMock.expectOne('/api/channels/b/emotes/active-set');
    httpMock
      .expectOne('/api/channels/b/permissions')
      .flush({ canManage: true, canViewUsageStats: true });

    // --- Now A's stale answer lands. refreshSetStatus() must discard it: channelName() is 'b'. ---
    staleStatusReq.flush(
      setStatus({ activeEmoteSetId: 'set-a-REFRESHED', trackedSince: '2026-01-01T00:00:00Z' }),
    );

    // Still A's *pre-refresh* status — the stale answer must not have landed under B's name, and it
    // must not have landed at all (setStatusChannel is unchanged, still naming A from the mount-time
    // fetch, not bumped to 'b' by the discarded response).
    expect(component['setStatusChannel']()).toBe('a');
    expect(component['setStatus']()?.activeEmoteSetId).toBe('set-a');

    // --- B's own answer lands and is what the page adopts. ---
    freshStatusReqB.flush(
      setStatus({ activeEmoteSetId: 'set-b', trackedSince: '2026-02-01T00:00:00Z' }),
    );
    // Two ticks: the first runs the "all time" effect that corrects from() against B's trackedSince,
    // the second reruns the load effect (which reads the now-corrected from()) and fires B's totals
    // and series requests — see the "all time" effect's own comment for why this is a second run
    // rather than the same one.
    fixture.detectChanges();
    fixture.detectChanges();

    flushByPath(httpMock, '/api/channels/b/usage-stats/totals', []);
    flushByPath(httpMock, '/api/channels/b/usage-stats/series', {
      from: '2026-02-01',
      to: '2026-09-08',
      liveDays: [],
      emotes: [],
    });

    expect(component['setStatus']()?.activeEmoteSetId).toBe('set-b');
    expect(component['setStatusChannel']()).toBe('b');
    expect(component['importScopeCurrent']()).toBe(true);
  });

  it("discards A's late refreshSetStatus() answer arriving after B's own load has already finished", () => {
    // --- Mount on channel A: permissions + the initial getSetStatus + totals/series once the
    // "all time" range resolves against A's trackedSince. Identical to the "before" case above —
    // duplicated rather than shared, so the flush order at each call site stays visible. ---
    httpMock
      .expectOne('/api/channels/a/permissions')
      .flush({ canManage: true, canViewUsageStats: true });

    httpMock
      .expectOne('/api/channels/a/emotes/active-set')
      .flush(setStatus({ activeEmoteSetId: 'set-a', trackedSince: '2026-01-01T00:00:00Z' }));
    fixture.detectChanges();

    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', []);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', {
      from: '2026-01-01',
      to: '2026-09-08',
      liveDays: [],
      emotes: [],
    });

    // --- A channel.synced burst on A's live stream fires refreshSetStatus() for A. Leave that
    // request pending — this is the in-flight request B's full load below must outrun. ---
    const sourceA = FakeEventSource.instances[0];
    sourceA.emit({ type: LIVE_EVENT_TYPES.channelSynced, channel: 'a' });
    vi.advanceTimersByTime(1000); // CHANNEL_RELOAD_DEBOUNCE_MS
    fixture.detectChanges();

    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', []);

    const staleStatusReq = httpMock.expectOne('/api/channels/a/emotes/active-set');

    // --- Switch channels within the same route before A's refresh answers. ---
    fixture.componentRef.setInput('channelName', 'b');
    fixture.detectChanges();

    // --- Unlike the "before" case, B's own load is driven all the way to completion here: status,
    // both change-detection ticks, and totals/series. This is what puts setStatusChannel on 'b'
    // *before* A's stale answer lands, and it is the ordering #112 actually reported. ---
    httpMock
      .expectOne('/api/channels/b/emotes/active-set')
      .flush(setStatus({ activeEmoteSetId: 'set-b', trackedSince: '2026-02-01T00:00:00Z' }));
    httpMock
      .expectOne('/api/channels/b/permissions')
      .flush({ canManage: true, canViewUsageStats: true });
    fixture.detectChanges();
    fixture.detectChanges();

    flushByPath(httpMock, '/api/channels/b/usage-stats/totals', []);
    flushByPath(httpMock, '/api/channels/b/usage-stats/series', {
      from: '2026-02-01',
      to: '2026-09-08',
      liveDays: [],
      emotes: [],
    });

    // B has genuinely, fully landed — importScopeCurrent() is true here for the right reason,
    // before A's stale answer is even in the picture.
    expect(component['setStatus']()?.activeEmoteSetId).toBe('set-b');
    expect(component['setStatusChannel']()).toBe('b');
    expect(component['importScopeCurrent']()).toBe(true);

    // --- Only now does A's stale answer land. Pre-fix, this is the dangerous window: the old code
    // wrote setStatus unconditionally but never touched setStatusChannel, so setStatusChannel was
    // already 'b' here and importScopeIsCurrent('b', 'b', 'b') kept reporting true — over A's set id.
    // The fix's channel-freeze guard must drop this answer instead. ---
    staleStatusReq.flush(
      setStatus({ activeEmoteSetId: 'set-a-REFRESHED', trackedSince: '2026-01-01T00:00:00Z' }),
    );

    expect(component['setStatus']()?.activeEmoteSetId).toBe('set-b');
    expect(component['setStatusChannel']()).toBe('b');
    expect(component['importScopeCurrent']()).toBe(true);
  });

  it('lets a channel.synced-triggered refreshSetStatus() claim the channel after the initial status fetch failed', () => {
    // --- Mount on channel A, but the initial getSetStatus fails. load()'s error branch sets
    // setStatus to null and setStatusFailedChannel to 'a', deliberately leaving setStatusChannel
    // untouched (see its own comment) — nothing has "claimed" the channel yet. ---
    httpMock
      .expectOne('/api/channels/a/permissions')
      .flush({ canManage: true, canViewUsageStats: true });

    httpMock
      .expectOne('/api/channels/a/emotes/active-set')
      .flush(null, { status: 500, statusText: 'Server Error' });
    // setStatusFailedChannel flips rangeResolved true for this channel (see its own comment), so
    // the same effect rerun that absorbed the error also fires totals/series against the
    // still-placeholder range — nothing ever corrected from() since setStatus stayed null.
    fixture.detectChanges();

    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', []);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', {
      from: '2025-09-09',
      to: '2026-09-08',
      liveDays: [],
      emotes: [],
    });

    expect(component['setStatusChannel']()).toBeNull();
    expect(component['importScopeCurrent']()).toBe(false);

    // --- A channel.synced burst on A's live stream fires refreshSetStatus() for A. ---
    const sourceA = FakeEventSource.instances[0];
    sourceA.emit({ type: LIVE_EVENT_TYPES.channelSynced, channel: 'a' });
    vi.advanceTimersByTime(1000); // CHANNEL_RELOAD_DEBOUNCE_MS
    fixture.detectChanges();

    // The same burst also re-triggers a quiet totals reload — drain it, it is not part of what
    // this test is pinning down.
    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', []);

    // --- The silent refresh succeeds this time. ---
    httpMock
      .expectOne('/api/channels/a/emotes/active-set')
      .flush(setStatus({ activeEmoteSetId: 'set-a', trackedSince: '2026-01-01T00:00:00Z' }));

    // The successful refresh is what finally gets to name the channel — setStatusChannel was
    // deliberately left null by the earlier failure (load()'s error branch, see its own comment),
    // and this write is the fix's other half: refreshSetStatus()'s success branch mirrors load()'s
    // own, writing setStatusChannel alongside setStatus rather than leaving it stale.
    expect(component['setStatus']()?.activeEmoteSetId).toBe('set-a');
    expect(component['setStatusChannel']()).toBe('a');
    // totalsChannel was already 'a' — set alongside the totals fired earlier once the failure
    // resolved rangeResolved — so this is also where importScopeCurrent() turns true.
    expect(component['importScopeCurrent']()).toBe(true);
  });
});

/**
 * #94: a silent reload (`preserveSelection: true`) must reconcile the selection against the
 * emotes it actually loaded, not leave a since-deleted emote's key sitting in `selectedKeys()`
 * forever — and it must not overcorrect by dropping a row that only fell out of the *filtered*
 * view, which is a completely different, already-solved case (`retainVisible()`, S2-16).
 *
 * Separate `describe`/`beforeEach` from the block above, matching this spec's own established
 * choice to duplicate mount choreography per scenario rather than share it (see the file doc) —
 * each test's totals/status payload differs enough that a shared setup would obscure more than it
 * saves.
 */
describe('UsageStatsPage — silent reload reconciles the selection (#94)', () => {
  let fixture: ComponentFixture<UsageStatsPage>;
  let component: UsageStatsPage;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    FakeEventSource.instances = [];
    vi.stubGlobal('ResizeObserver', FakeResizeObserver);
    vi.useFakeTimers();

    TestBed.configureTestingModule({
      imports: [
        TranslocoTestingModule.forRoot({
          langs: { de: {} },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          provide: EVENT_SOURCE_FACTORY,
          useValue: (url: string) => new FakeEventSource(url) as unknown as EventSource,
        },
      ],
    });

    TestBed.overrideComponent(UsageStatsPage, {
      set: { template: '<div #sheet></div><div #stickyBar></div>' },
    });

    fixture = TestBed.createComponent(UsageStatsPage);
    component = fixture.componentInstance;
    httpMock = TestBed.inject(HttpTestingController);

    fixture.componentRef.setInput('channelName', 'a');
    fixture.detectChanges();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  /**
   * Drives the page through a full mount on channel 'a' with the given totals. `botsExcludedSince`/
   * `sharedChatSeparatedSince` are both given a date so `SetStatusFlushProbeGate.shouldRefreshOn`
   * short-circuits to `false` and the reload each test fires afterwards produces no extra
   * `/emotes/active-set` request — the scenario under test is the totals reconciliation alone.
   */
  function mount(totals: EmoteUsageTotal[]): void {
    httpMock
      .expectOne('/api/channels/a/permissions')
      .flush({ canManage: true, canViewUsageStats: true });

    httpMock.expectOne('/api/channels/a/emotes/active-set').flush(
      setStatus({
        activeEmoteSetId: 'set-a',
        trackedSince: '2026-01-01T00:00:00Z',
        botsExcludedSince: '2026-01-02T00:00:00Z',
        sharedChatSeparatedSince: '2026-01-02T00:00:00Z',
      }),
    );
    fixture.detectChanges();

    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', totals);
    flushByPath(httpMock, '/api/channels/a/usage-stats/series', {
      from: '2026-01-01',
      to: '2026-09-08',
      liveDays: [],
      emotes: [],
    });
  }

  /** Fires one `usageFlushed` burst (the silent, `preserveSelection`d reload path) and flushes its
   *  totals response — the same round trip `loadTotals(..., {preserveSelection: true, silent:
   *  true})` produces from the live subscription in the constructor. */
  function silentReload(totals: EmoteUsageTotal[]): void {
    FakeEventSource.instances[0].emit({ type: LIVE_EVENT_TYPES.usageFlushed, channel: 'a' });
    vi.advanceTimersByTime(CHANNEL_RELOAD_DEBOUNCE_MS);
    fixture.detectChanges();
    flushByPath(httpMock, '/api/channels/a/usage-stats/totals', totals);
  }

  it('reconciles a full pre-reload selection against what the reload actually returned', () => {
    const a = emote('a', 'PeepoA');
    const b = emote('b', 'PeepoB');
    const c = emote('c', 'PeepoC');

    mount([a, b, c]);
    component['selection'].onRowClick(a, { shiftKey: false } as MouseEvent);
    component['selection'].onRowClick(c, { shiftKey: false } as MouseEvent);
    expect(component['selection'].selectedKeys().sort()).toEqual(['a', 'c']);
    expect(component['selectionPrunedFeedback']()).toBeNull();

    // The silent reload comes back without 'c' — deleted externally on 7TV between loads.
    silentReload([a, b]);

    // 'a' survives, 'c' is gone from the authoritative key set, and the transient feedback names
    // exactly one dropped emote.
    expect(component['selection'].selectedKeys()).toEqual(['a']);
    expect(component['selectionPrunedFeedback']()).toEqual({
      key: 'usageStats.selectionPruned.one',
      count: 1,
    });

    // The feedback is transient — it clears itself after SELECTION_PRUNED_FEEDBACK_MS.
    vi.advanceTimersByTime(4000);
    expect(component['selectionPrunedFeedback']()).toBeNull();
  });

  it('a silent reload that loses nothing selected leaves the selection and the feedback alone', () => {
    const a = emote('a', 'PeepoA');
    const b = emote('b', 'PeepoB');

    mount([a, b]);
    component['selection'].onRowClick(a, { shiftKey: false } as MouseEvent);

    silentReload([a, b]);

    expect(component['selection'].selectedKeys()).toEqual(['a']);
    expect(component['selectionPrunedFeedback']()).toBeNull();
  });

  it('a merely filtered-out but still-loaded emote survives a silent reload and shows no feedback', () => {
    // This is the case that decides retainAmong(emotes) over retainVisible()/atlasOrder(): 'c'
    // starts above the min-usage filter, gets selected, and the reload lowers its count below that
    // same filter — so it drops out of atlasOrder() (the filtered view) while still being part of
    // the reloaded, unfiltered set. Reconciling against atlasOrder() would wrongly report it as
    // "gone" (#94); reconciling against the raw reload payload must not.
    const a = emote('a', 'PeepoA', 10);
    const c = emote('c', 'PeepoC', 10);

    mount([a, c]);

    // A min-usage filter of 5 — both emotes currently clear it, so retainVisible() (fired by the
    // filter change itself, unrelated to the reload below) does not touch the selection made next.
    component['usageFilter'].setRange(5, null);
    component['selection'].onRowClick(c, { shiftKey: false } as MouseEvent);
    expect(component['selection'].selectedKeys()).toEqual(['c']);
    expect(component['atlasOrder']().map((e: EmoteUsageTotal) => e.emoteId)).toContain('c');

    // The reload drops 'c's count under the filter's floor — atlasOrder() will no longer include
    // it — but 'c' itself is still present in the reloaded payload.
    silentReload([a, emote('c', 'PeepoC', 1)]);

    // Confirms the filter really did narrow atlasOrder() past 'c' — otherwise this test would not
    // be exercising the case it claims to.
    expect(component['atlasOrder']().map((e: EmoteUsageTotal) => e.emoteId)).not.toContain('c');
    // ...yet the selection and the feedback are both untouched: 'c' was never actually removed
    // from the set, only filtered out of the current view.
    expect(component['selection'].selectedKeys()).toEqual(['c']);
    expect(component['selectionPrunedFeedback']()).toBeNull();
  });
});
