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

  // Reassigning [ngSrc] aborts whatever request was still in flight (HTML's "update the image data"
  // algorithm), so the browser never dispatches load/error for a url this element has moved past —
  // only the url it is currently on can ever complete. This models that: the pointer moves to B
  // before A finishes, and only once B's own (legitimate) load fires does the sprite reveal B's art.
  it('reveals the new emote once its own load fires, not the previous one', () => {
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
