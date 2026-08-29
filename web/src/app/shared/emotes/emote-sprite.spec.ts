import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';

import { EmoteSprite } from './emote-sprite';

@Component({
  imports: [EmoteSprite],
  template: `<app-emote-sprite [url]="url()" [size]="64" />`,
})
class Host {
  readonly url = signal('https://cdn.7tv.app/emote/aaa/2x.webp');
}

describe('EmoteSprite', () => {
  let fixture: ComponentFixture<Host>;
  let host: Host;

  function image(): HTMLImageElement {
    return fixture.nativeElement.querySelector('img');
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [Host] }).compileComponents();
    fixture = TestBed.createComponent(Host);
    host = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('keeps the image invisible until it has loaded', () => {
    // It stays in the DOM — removing it would mean it never starts loading — and it keeps its box,
    // so revealing it costs no layout shift.
    expect(image().style.visibility).toBe('hidden');
  });

  it('reveals the image once it has loaded', () => {
    image().dispatchEvent(new Event('load'));
    fixture.detectChanges();

    expect(image().style.visibility).toBe('');
  });

  // The actual bug: the sidecar's <img> is never rebuilt, so a plain [ngSrc] swap left the previous
  // emote's bitmap on screen next to the new emote's numbers.
  it('hides the previous emote again the moment the url changes', () => {
    image().dispatchEvent(new Event('load'));
    fixture.detectChanges();
    expect(image().style.visibility).toBe('');

    host.url.set('https://cdn.7tv.app/emote/bbb/2x.webp');
    fixture.detectChanges();

    expect(image().style.visibility).toBe('hidden');
  });

  it('leaves a broken image hidden so the plate shows through', () => {
    image().dispatchEvent(new Event('error'));
    fixture.detectChanges();

    expect(image().style.visibility).toBe('hidden');
  });

  // The reveal after a url change, which is the sequence the sidecar actually runs: hidden on the
  // swap, visible again once a load fires. It does NOT distinguish whose load that was — the
  // handler is `loadedUrl.set(url())` and a synthetic jsdom `load` carries no identity of its own.
  // Nothing here needs to: reassigning [ngSrc] aborts whatever request was in flight (HTML's
  // "update the image data" algorithm), so the browser never dispatches load/error for a url the
  // element has already moved past.
  it('reveals the sprite again once a load fires after a url change', () => {
    host.url.set('https://cdn.7tv.app/emote/bbb/2x.webp');
    fixture.detectChanges();
    expect(image().style.visibility).toBe('hidden');

    image().dispatchEvent(new Event('load'));
    fixture.detectChanges();

    expect(image().style.visibility).toBe('');
  });

  it('stays hidden if the url moves on again before the current one ever loaded', () => {
    host.url.set('https://cdn.7tv.app/emote/bbb/2x.webp');
    fixture.detectChanges();

    host.url.set('https://cdn.7tv.app/emote/ccc/2x.webp');
    fixture.detectChanges();

    expect(image().style.visibility).toBe('hidden');
  });

  // The component's most subtle invariant: settled is keyed on url identity, not on "has a load ever
  // fired", so returning to an already-loaded url reveals immediately rather than waiting on another
  // load event — which a cached image will still deliver asynchronously, but the UI shouldn't
  // visibly wait on when it already has the pixels.
  it('reveals immediately when the url returns to one that already loaded', () => {
    image().dispatchEvent(new Event('load'));
    fixture.detectChanges();

    host.url.set('https://cdn.7tv.app/emote/bbb/2x.webp');
    fixture.detectChanges();
    expect(image().style.visibility).toBe('hidden');

    host.url.set('https://cdn.7tv.app/emote/aaa/2x.webp');
    fixture.detectChanges();

    expect(image().style.visibility).toBe('');
  });
});

@Component({
  imports: [EmoteSprite],
  template: `<app-emote-sprite [url]="'https://cdn.7tv.app/emote/aaa/4x.webp'" [size]="64" />`,
})
class ResponsiveHost {}

describe('EmoteSprite responsive srcset', () => {
  let fixture: ComponentFixture<ResponsiveHost>;

  function image(): HTMLImageElement {
    return fixture.nativeElement.querySelector('img');
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [ResponsiveHost] }).compileComponents();
    fixture = TestBed.createComponent(ResponsiveHost);
    fixture.detectChanges();
  });

  // `sizes` is bound to the component's own edge length so NgOptimizedImage builds a width-based
  // srcset instead of a density one — the whole point of this component-local IMAGE_CONFIG. No
  // `auto, ` prefix here: Angular only adds that for `loading="lazy"` (the directive's default),
  // and this component deliberately overrides that to `eager` (see below) so the browser has a
  // resolved `sizes` value from the first layout instead of guessing high and re-fetching.
  it('sets sizes to the component edge length, with no lazy-loading auto prefix', () => {
    expect(image().getAttribute('sizes')).toBe('64px');
  });

  // The actual point of `loading="eager"` here: not scheduling (measured separately to be a
  // non-factor), but keeping Angular from prepending `auto, ` to `sizes` — see the doc comment on
  // `EmoteSprite`. Guards against silently reverting to `lazy` and reintroducing the double-fetch.
  it('sets loading to eager so sizes resolves without a layout round-trip', () => {
    expect(image().getAttribute('loading')).toBe('eager');
  });

  // One candidate per configured breakpoint (32/64/96/128), each rewritten through the same
  // width -> variant mapping emoteVariantUrl uses elsewhere, rather than the two-candidate density
  // srcset the same setup used to produce.
  it('builds a width-descriptor srcset covering all four 7TV variants', () => {
    expect(image().getAttribute('srcset')).toBe(
      [
        'https://cdn.7tv.app/emote/aaa/1x.webp 32w',
        'https://cdn.7tv.app/emote/aaa/2x.webp 64w',
        'https://cdn.7tv.app/emote/aaa/3x.webp 96w',
        'https://cdn.7tv.app/emote/aaa/4x.webp 128w',
      ].join(', '),
    );
  });
});

@Component({
  imports: [EmoteSprite],
  template: `
    <app-emote-sprite
      [url]="'https://cdn.7tv.app/emote/aaa/2x.webp'"
      [size]="64"
      [dimmed]="dimmed()"
      spriteClass="custom-class"
    />
  `,
})
class StyledHost {
  readonly dimmed = signal(false);
}

describe('EmoteSprite styling inputs', () => {
  let fixture: ComponentFixture<StyledHost>;
  let host: StyledHost;

  function image(): HTMLImageElement {
    return fixture.nativeElement.querySelector('img');
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [StyledHost] }).compileComponents();
    fixture = TestBed.createComponent(StyledHost);
    host = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('applies a custom spriteClass instead of the default', () => {
    expect(image().className).toContain('custom-class');
  });

  it('applies the dimmed class for an archived ballot member', () => {
    host.dimmed.set(true);
    fixture.detectChanges();

    expect(image().classList.contains('opacity-40')).toBe(true);
  });
});
