import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { PointerModeService } from './pointer-mode.service';

/**
 * Same shape as the fake in core/theme/theme.service.spec.ts: jsdom has no matchMedia, and a real
 * listener set is what lets the change and teardown cases assert on behaviour rather than on call
 * counts. The global stub in test-setup.ts always reports `matches: false`, so anything asserting a
 * coarse pointer has to install its own.
 */
class FakeMediaQueryList {
  readonly listeners = new Set<(event: MediaQueryListEvent) => void>();

  constructor(public matches: boolean) {}

  addEventListener(_type: 'change', listener: (event: MediaQueryListEvent) => void): void {
    this.listeners.add(listener);
  }

  removeEventListener(_type: 'change', listener: (event: MediaQueryListEvent) => void): void {
    this.listeners.delete(listener);
  }

  emit(matches: boolean): void {
    this.matches = matches;
    for (const listener of this.listeners) {
      listener({ matches } as MediaQueryListEvent);
    }
  }
}

let coarseQuery: FakeMediaQueryList;
let queriedFor: string[];

function installMatchMedia(coarse: boolean): void {
  coarseQuery = new FakeMediaQueryList(coarse);
  queriedFor = [];
  vi.stubGlobal('matchMedia', (query: string) => {
    queriedFor.push(query);
    return coarseQuery;
  });
}

describe('PointerModeService', () => {
  beforeEach(() => TestBed.resetTestingModule());

  afterEach(() => {
    vi.unstubAllGlobals();
    TestBed.resetTestingModule();
  });

  it('asks for the primary pointing device, not for any of them', () => {
    installMatchMedia(false);
    TestBed.inject(PointerModeService);

    // `any-pointer: coarse` would also be true for a desktop with a touchscreen attached, which is
    // exactly the machine that still has DevTools and must keep the delete engine.
    expect(queriedFor).toContain('(pointer: coarse)');
  });

  it('reports a mouse as not coarse', () => {
    installMatchMedia(false);

    expect(TestBed.inject(PointerModeService).isCoarse()).toBe(false);
  });

  it('reports a finger as coarse', () => {
    installMatchMedia(true);

    expect(TestBed.inject(PointerModeService).isCoarse()).toBe(true);
  });

  it('follows a change of pointing device', () => {
    installMatchMedia(true);
    const service = TestBed.inject(PointerModeService);
    expect(service.isCoarse()).toBe(true);

    // Plugging a mouse into a tablet, or the browser's device emulation being switched off.
    coarseQuery.emit(false);

    expect(service.isCoarse()).toBe(false);
  });

  it('drops the media listener when the injector goes down', () => {
    installMatchMedia(true);
    TestBed.inject(PointerModeService);
    expect(coarseQuery.listeners.size).toBe(1);

    TestBed.resetTestingModule();

    expect(coarseQuery.listeners.size).toBe(0);
  });
});
