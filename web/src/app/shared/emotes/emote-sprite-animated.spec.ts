import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { EmoteSpriteAnimated } from './emote-sprite-animated';

const ANIMATED_A = 'https://cdn.7tv.app/emote/aaa/4x_static.webp';
const ANIMATED_B = 'https://cdn.7tv.app/emote/bbb/4x_static.webp';
const STILL_ONLY = 'https://cdn.7tv.app/emote/ccc/4x.webp';

@Component({
  imports: [EmoteSpriteAnimated],
  template: `<app-emote-sprite-animated [url]="url()" [size]="56" />`,
})
class Host {
  readonly url = signal(ANIMATED_A);
}

describe('EmoteSpriteAnimated', () => {
  let fixture: ComponentFixture<Host>;
  let host: Host;

  function sources(): string[] {
    return [...fixture.nativeElement.querySelectorAll('img')].map(
      (img) => (img as HTMLImageElement).getAttribute('src') ?? '',
    );
  }

  beforeEach(async () => {
    vi.useFakeTimers();
    await TestBed.configureTestingModule({ imports: [Host] }).compileComponents();
    fixture = TestBed.createComponent(Host);
    host = fixture.componentInstance;
    fixture.detectChanges();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  // The still is what the atlas already loaded for this emote, so it paints from cache. Asking for
  // the animation in the same breath would spend up to 1.2 MB on a pointer that is merely passing.
  it('shows only the still before the dwell has passed', () => {
    expect(sources()).toEqual([ANIMATED_A]);
  });

  it('adds the animation once the pointer has rested', () => {
    vi.advanceTimersByTime(200);
    fixture.detectChanges();

    expect(sources()).toEqual([ANIMATED_A, 'https://cdn.7tv.app/emote/aaa/2x.webp']);
  });

  // A sweep across the atlas rebinds this per cell. Without the reset, every cell touched on the
  // way would still fire its request a moment later.
  it('never requests an animation the pointer moved off before the dwell', () => {
    host.url.set(ANIMATED_B);
    fixture.detectChanges();
    vi.advanceTimersByTime(200);
    fixture.detectChanges();

    expect(sources()).not.toContain('https://cdn.7tv.app/emote/aaa/2x.webp');
  });

  it('drops the previous animation the moment the emote changes', () => {
    vi.advanceTimersByTime(200);
    fixture.detectChanges();

    host.url.set(ANIMATED_B);
    fixture.detectChanges();

    expect(sources()).toEqual([ANIMATED_B]);
  });

  // A still emote has no animated variant to fetch — animatedEmoteUrl hands back the same url, and
  // a second <img> on it would be a duplicate request for a picture already on screen.
  it('never stacks a second image on a still emote', () => {
    host.url.set(STILL_ONLY);
    fixture.detectChanges();
    vi.advanceTimersByTime(200);
    fixture.detectChanges();

    expect(sources()).toEqual([STILL_ONLY]);
  });
});
