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

  it('ignores a load that belongs to a url already superseded', () => {
    const stale = image();
    host.url.set('https://cdn.7tv.app/emote/bbb/2x.webp');
    fixture.detectChanges();

    // The slow first request finishing after the pointer has already moved on.
    stale.dispatchEvent(new Event('load'));
    fixture.detectChanges();

    expect(image().style.visibility).toBe('hidden');
  });

  it('leaves a broken image hidden so the plate shows through', () => {
    image().dispatchEvent(new Event('error'));
    fixture.detectChanges();

    expect(image().style.visibility).toBe('hidden');
  });
});
