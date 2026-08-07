import { DialogRef } from '@angular/cdk/dialog';
import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { SHEET_DISMISS_DISTANCE_PX, SHEET_MIN_TRAVEL_PX } from './sheet-drag-policy';
import { SheetDrag } from './sheet-drag';

// jsdom has no pointer-capture implementation at all (not even a no-op) — stub it so the directive's
// setPointerCapture/releasePointerCapture calls do not throw "not a function" in every test.
function stubPointerCapture(): {
  set: ReturnType<typeof vi.fn>;
  release: ReturnType<typeof vi.fn>;
} {
  const set = vi.fn();
  const release = vi.fn();
  HTMLElement.prototype.setPointerCapture = set;
  HTMLElement.prototype.releasePointerCapture = release;
  return { set, release };
}

@Component({
  imports: [SheetDrag],
  template: `
    <div class="cdk-overlay-pane">
      <div [appSheetDrag]="enabled()">
        <div data-sheet-handle>Handle</div>
        <div class="content">Content</div>
      </div>
    </div>
  `,
})
class Host {
  readonly enabled = signal(true);
}

describe('SheetDrag', () => {
  let closeSpy: ReturnType<typeof vi.fn>;
  let capture: { set: ReturnType<typeof vi.fn>; release: ReturnType<typeof vi.fn> };
  let fixture: ComponentFixture<Host>;
  let pane: HTMLElement;
  let handle: HTMLElement;
  let content: HTMLElement;

  beforeEach(async () => {
    capture = stubPointerCapture();
    closeSpy = vi.fn();

    await TestBed.configureTestingModule({
      imports: [Host],
      providers: [{ provide: DialogRef, useValue: { close: closeSpy } }],
    }).compileComponents();

    fixture = TestBed.createComponent(Host);
    fixture.detectChanges();

    pane = fixture.nativeElement.querySelector('.cdk-overlay-pane');
    handle = fixture.nativeElement.querySelector('[data-sheet-handle]');
    content = fixture.nativeElement.querySelector('.content');
  });

  afterEach(() => {
    // Own prototype patch, own cleanup — do not leak the stub into unrelated spec files.
    delete (HTMLElement.prototype as { setPointerCapture?: unknown }).setPointerCapture;
    delete (HTMLElement.prototype as { releasePointerCapture?: unknown }).releasePointerCapture;
  });

  function down(target: HTMLElement, clientY: number, pointerId = 1): void {
    target.dispatchEvent(new PointerEvent('pointerdown', { pointerId, clientY, bubbles: true }));
  }

  function move(target: HTMLElement, clientY: number, pointerId = 1): void {
    target.dispatchEvent(new PointerEvent('pointermove', { pointerId, clientY, bubbles: true }));
  }

  function up(target: HTMLElement, pointerId = 1): void {
    target.dispatchEvent(new PointerEvent('pointerup', { pointerId, bubbles: true }));
  }

  function cancel(target: HTMLElement, pointerId = 1): void {
    target.dispatchEvent(new PointerEvent('pointercancel', { pointerId, bubbles: true }));
  }

  it('does nothing at all while disabled — no capture, no transform, gesture never starts', () => {
    fixture.componentInstance.enabled.set(false);
    fixture.detectChanges();

    down(handle, 0);
    move(handle, 200);

    expect(capture.set).not.toHaveBeenCalled();
    expect(pane.style.transform).toBe('');
  });

  it('follows the pointer once a gesture starts on the handle', () => {
    down(handle, 0);
    move(handle, 40);

    expect(capture.set).toHaveBeenCalledWith(1);
    expect(pane.style.transform).toBe('translateY(40px)');
  });

  it('clamps to zero — no resistance upward past the docked edge', () => {
    down(handle, 100);
    move(handle, 40);

    expect(pane.style.transform).toBe('translateY(0px)');
  });

  it('springs back and does not close when released below the thresholds', () => {
    down(handle, 0);
    move(handle, SHEET_MIN_TRAVEL_PX - 1);
    up(handle);

    expect(pane.style.transform).toBe('');
    expect(closeSpy).not.toHaveBeenCalled();
  });

  it('closes the dialog when the drag clears the distance threshold', () => {
    down(handle, 0);
    move(handle, SHEET_DISMISS_DISTANCE_PX);
    up(handle);

    expect(closeSpy).toHaveBeenCalledTimes(1);
  });

  it('starts a gesture from the content when the pane is already scrolled to the top', () => {
    pane.scrollTop = 0;

    down(content, 0);
    move(content, 40);

    expect(pane.style.transform).toBe('translateY(40px)');
  });

  it('refuses to start from the content when the pane is scrolled — that is a scroll gesture', () => {
    pane.scrollTop = 10;

    down(content, 0);
    move(content, 40);

    expect(capture.set).not.toHaveBeenCalled();
    expect(pane.style.transform).toBe('');
  });

  it('starts from the handle regardless of scroll position', () => {
    pane.scrollTop = 10;

    down(handle, 0);
    move(handle, 40);

    expect(pane.style.transform).toBe('translateY(40px)');
  });

  it('ignores a second pointer while one gesture is already in flight', () => {
    down(handle, 0, 1);
    move(handle, 40, 1);

    down(handle, 5, 2);
    move(handle, 999, 2);

    // The second pointer's move must not have overwritten the first pointer's tracked distance.
    expect(pane.style.transform).toBe('translateY(40px)');
  });

  it('cancels cleanly — no leftover transform, no dialog close, on a short drag interrupted mid-gesture', () => {
    down(handle, 0);
    move(handle, SHEET_MIN_TRAVEL_PX - 1);
    cancel(handle);

    expect(pane.style.transform).toBe('');
    expect(pane.style.transition).toBe('');
    expect(closeSpy).not.toHaveBeenCalled();
  });

  it('leaves no transform and releases capture when the component is destroyed mid-drag', () => {
    down(handle, 0);
    move(handle, SHEET_MIN_TRAVEL_PX - 1);

    fixture.destroy();

    expect(pane.style.transform).toBe('');
    expect(pane.style.transition).toBe('');
    expect(capture.release).toHaveBeenCalledWith(1);
  });

  it('does nothing on destroy when no gesture was in progress', () => {
    fixture.destroy();

    expect(capture.release).not.toHaveBeenCalled();
  });
});
