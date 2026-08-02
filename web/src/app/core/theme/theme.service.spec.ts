import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { ResolvedTheme, ThemeService, resolveInitialPreference } from './theme.service';

/**
 * jsdom does not implement matchMedia at all, so it is not so much stubbed as supplied. Keeping a
 * real listener set (rather than a vi.fn()) is what lets the system-change and the teardown tests
 * assert on behaviour instead of on call counts.
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

  /** What the OS switching between light and dark looks like from in here. */
  emit(matches: boolean): void {
    this.matches = matches;
    for (const listener of this.listeners) {
      listener({ matches } as MediaQueryListEvent);
    }
  }
}

let darkQuery: FakeMediaQueryList;
let queriedFor: string[];

function installMatchMedia(systemPrefersDark: boolean): void {
  darkQuery = new FakeMediaQueryList(systemPrefersDark);
  queriedFor = [];
  vi.stubGlobal('matchMedia', (query: string) => {
    queriedFor.push(query);
    return darkQuery;
  });
}

function installThemeColorMetas(): void {
  for (const [mode, content] of [
    ['dark', '#020617'],
    ['light', '#f8fafc'],
  ]) {
    const meta = document.createElement('meta');
    meta.name = 'theme-color';
    meta.dataset['themeMode'] = mode;
    meta.content = content;
    meta.media = `(prefers-color-scheme: ${mode})`;
    document.head.appendChild(meta);
  }
}

function metaMediaFor(mode: 'dark' | 'light'): string {
  return (
    document.querySelector<HTMLMetaElement>(`meta[name="theme-color"][data-theme-mode="${mode}"]`)
      ?.media ?? ''
  );
}

function currentAttribute(): ResolvedTheme {
  return document.documentElement.dataset['theme'] as ResolvedTheme;
}

describe('resolveInitialPreference', () => {
  beforeEach(() => localStorage.clear());

  it('defaults to following the system when nothing is stored', () => {
    // Not 'dark': ignoring prefers-color-scheme would mean throwing away the only signal there is
    // before the user has said anything.
    expect(resolveInitialPreference()).toBe('system');
  });

  it('prefers an explicitly stored choice', () => {
    localStorage.setItem('emotepurge.theme', 'light');

    expect(resolveInitialPreference()).toBe('light');
  });

  it('ignores a stored value that is not a known preference', () => {
    // Anyone can hand-edit localStorage; junk must not become the active theme.
    localStorage.setItem('emotepurge.theme', 'sepia');

    expect(resolveInitialPreference()).toBe('system');
  });
});

describe('ThemeService', () => {
  beforeEach(() => {
    localStorage.clear();
    document.documentElement.removeAttribute('data-theme');
    document.head.querySelectorAll('meta[name="theme-color"]').forEach((meta) => meta.remove());
    installThemeColorMetas();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    TestBed.resetTestingModule();
  });

  it('resolves to the system preference while no explicit choice exists', () => {
    installMatchMedia(false);
    const service = TestBed.inject(ThemeService);

    expect(queriedFor).toContain('(prefers-color-scheme: dark)');
    expect(service.preference()).toBe('system');
    expect(service.resolved()).toBe('light');
  });

  it('lets an explicit choice win over a contradicting system preference', () => {
    installMatchMedia(true);
    localStorage.setItem('emotepurge.theme', 'light');
    const service = TestBed.inject(ThemeService);

    expect(service.resolved()).toBe('light');
  });

  it('writes the resolved theme onto <html>, which is where the token block hangs off', () => {
    installMatchMedia(true);
    TestBed.inject(ThemeService);
    TestBed.tick();

    // Not the shell's root div: the CDK overlay container lives outside it, so dialogs would
    // otherwise keep the mode they were not opened in.
    expect(currentAttribute()).toBe('dark');
  });

  it('setPreference updates the signal, the attribute and localStorage together', () => {
    installMatchMedia(true);
    const service = TestBed.inject(ThemeService);
    TestBed.tick();

    service.setPreference('light');
    TestBed.tick();

    expect(service.preference()).toBe('light');
    expect(service.resolved()).toBe('light');
    expect(currentAttribute()).toBe('light');
    expect(localStorage.getItem('emotepurge.theme')).toBe('light');
  });

  it('persists the choice so the next visit skips the system guess', () => {
    installMatchMedia(true);
    TestBed.inject(ThemeService).setPreference('dark');

    expect(resolveInitialPreference()).toBe('dark');
  });

  it('follows a system change while the preference is system', () => {
    installMatchMedia(true);
    const service = TestBed.inject(ThemeService);
    TestBed.tick();
    expect(service.resolved()).toBe('dark');

    darkQuery.emit(false);
    TestBed.tick();

    expect(service.resolved()).toBe('light');
    expect(currentAttribute()).toBe('light');
  });

  it('ignores a system change once the user has chosen explicitly', () => {
    installMatchMedia(true);
    const service = TestBed.inject(ThemeService);
    service.setPreference('dark');
    TestBed.tick();

    darkQuery.emit(false);
    TestBed.tick();

    expect(service.resolved()).toBe('dark');
    expect(currentAttribute()).toBe('dark');
  });

  it('drops the media listener when the injector goes down', () => {
    installMatchMedia(true);
    TestBed.inject(ThemeService);
    expect(darkQuery.listeners.size).toBe(1);

    TestBed.resetTestingModule();

    expect(darkQuery.listeners.size).toBe(0);
  });

  it('leaves the theme-color metas media-driven while the preference is system', () => {
    installMatchMedia(true);
    TestBed.inject(ThemeService);
    TestBed.tick();

    // Both tags keep their query, which is what makes the system case work without any JavaScript.
    expect(metaMediaFor('dark')).toBe('(prefers-color-scheme: dark)');
    expect(metaMediaFor('light')).toBe('(prefers-color-scheme: light)');
  });

  it('forces the matching theme-color meta when the choice contradicts the system', () => {
    installMatchMedia(true);
    const service = TestBed.inject(ThemeService);

    service.setPreference('light');
    TestBed.tick();

    // Without this the dark tag would still match the system query and colour the browser chrome
    // for a mode the page is not in.
    expect(metaMediaFor('light')).toBe('all');
    expect(metaMediaFor('dark')).toBe('not all');
  });

  it('hands the theme-color metas back to the media queries when system is reselected', () => {
    installMatchMedia(true);
    const service = TestBed.inject(ThemeService);
    service.setPreference('light');
    TestBed.tick();

    service.setPreference('system');
    TestBed.tick();

    expect(metaMediaFor('dark')).toBe('(prefers-color-scheme: dark)');
    expect(metaMediaFor('light')).toBe('(prefers-color-scheme: light)');
  });
});
