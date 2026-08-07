import { DialogRef } from '@angular/cdk/dialog';
import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { SHEET_DISMISS_DISTANCE_PX, SHEET_MIN_TRAVEL_PX } from './sheet-drag-policy';
import { SheetDrag } from './sheet-drag';

// jsdom has no pointer-capture implementation at all (not even a no-op) — stub it so the directive's
// setPointerCapture/releasePointerCapture calls do not throw "not a function" in every test. Captures
// and restores whatever descriptor was there before (currently none), so this cannot outlive the spec
// file the day jsdom ships a real implementation of its own.
function patchPointerCapture(): {
  capture: { set: ReturnType<typeof vi.fn>; release: ReturnType<typeof vi.fn> };
  restore: () => void;
} {
  const setDescriptor = Object.getOwnPropertyDescriptor(HTMLElement.prototype, 'setPointerCapture');
  const releaseDescriptor = Object.getOwnPropertyDescriptor(
    HTMLElement.prototype,
    'releasePointerCapture',
  );
  const set = vi.fn();
  const release = vi.fn();
  HTMLElement.prototype.setPointerCapture = set;
  HTMLElement.prototype.releasePointerCapture = release;

  return {
    capture: { set, release },
    restore: () => {
      if (setDescriptor) {
        Object.defineProperty(HTMLElement.prototype, 'setPointerCapture', setDescriptor);
      } else {
        delete (HTMLElement.prototype as { setPointerCapture?: unknown }).setPointerCapture;
      }
      if (releaseDescriptor) {
        Object.defineProperty(HTMLElement.prototype, 'releasePointerCapture', releaseDescriptor);
      } else {
        delete (HTMLElement.prototype as { releasePointerCapture?: unknown }).releasePointerCapture;
      }
    },
  };
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

/** Sets up the Host fixture, optionally providing a DialogRef (defaults to a working stub). */
async function createFixture(
  dialogRefClose?: ReturnType<typeof vi.fn>,
): Promise<ComponentFixture<Host>> {
  await TestBed.configureTestingModule({
    imports: [Host],
    providers: dialogRefClose ? [{ provide: DialogRef, useValue: { close: dialogRefClose } }] : [],
  }).compileComponents();

  const fixture = TestBed.createComponent(Host);
  fixture.detectChanges();
  return fixture;
}

describe('SheetDrag', () => {
  let closeSpy: ReturnType<typeof vi.fn>;
  let capture: { set: ReturnType<typeof vi.fn>; release: ReturnType<typeof vi.fn> };
  let restoreCapture: () => void;
  let fixture: ComponentFixture<Host>;
  let pane: HTMLElement;
  let handle: HTMLElement;
  let content: HTMLElement;

  beforeEach(async () => {
    ({ capture, restore: restoreCapture } = patchPointerCapture());
    closeSpy = vi.fn();

    fixture = await createFixture(closeSpy);

    pane = fixture.nativeElement.querySelector('.cdk-overlay-pane');
    handle = fixture.nativeElement.querySelector('[data-sheet-handle]');
    content = fixture.nativeElement.querySelector('.content');
  });

  afterEach(() => {
    restoreCapture();
  });

  // `timeStamp` isn't settable through the PointerEventInit — it's assigned at construction — so
  // tests that need to control elapsed time override the read-only property afterwards. Real velocity
  // math is exercised through this, not mocked: a wrong `distance / elapsedMs` (dropped division,
  // divided by the wrong term, distance measured from the wrong origin) changes what these tests see.
  function withTimeStamp(event: Event, timeStamp: number): Event {
    Object.defineProperty(event, 'timeStamp', { value: timeStamp, configurable: true });
    return event;
  }

  function down(target: HTMLElement, clientY: number, pointerId = 1, timeStamp?: number): void {
    const event = new PointerEvent('pointerdown', { pointerId, clientY, bubbles: true });
    target.dispatchEvent(timeStamp === undefined ? event : withTimeStamp(event, timeStamp));
  }

  function move(target: HTMLElement, clientY: number, pointerId = 1): void {
    target.dispatchEvent(new PointerEvent('pointermove', { pointerId, clientY, bubbles: true }));
  }

  function up(target: HTMLElement, pointerId = 1, timeStamp?: number): void {
    const event = new PointerEvent('pointerup', { pointerId, bubbles: true });
    target.dispatchEvent(timeStamp === undefined ? event : withTimeStamp(event, timeStamp));
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

  it('dismisses a short drag released fast enough to clear the velocity threshold', () => {
    // 24 px in 40 ms is 0.6 px/ms — over SHEET_DISMISS_VELOCITY_PX_PER_MS (0.5), and the distance
    // (SHEET_MIN_TRAVEL_PX) stays well under SHEET_DISMISS_DISTANCE_PX (96), so only the velocity
    // branch can be responsible for the close.
    down(handle, 0, 1, 0);
    move(handle, SHEET_MIN_TRAVEL_PX);
    up(handle, 1, 40);

    expect(closeSpy).toHaveBeenCalledTimes(1);
  });

  it('springs back on the same short distance released too slowly to clear the velocity threshold', () => {
    // Same 24 px, but over 1000 ms — 0.024 px/ms, under both thresholds.
    down(handle, 0, 1, 0);
    move(handle, SHEET_MIN_TRAVEL_PX);
    up(handle, 1, 1000);

    expect(pane.style.transform).toBe('');
    expect(closeSpy).not.toHaveBeenCalled();
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

  it('clears the transform instead of parking it when there is no DialogRef to close', async () => {
    // A pane that isn't inside an Angular CDK Dialog (no DialogRef to inject) still runs the same
    // gesture — dismissing here must not leave the pane translated with nothing left to undo it.
    // The outer beforeEach already created and used a TestBed instance (with a DialogRef provided),
    // so a fresh module needs a reset first — TestBed refuses configuration after a component exists.
    TestBed.resetTestingModule();
    const bareFixture = await createFixture();
    const barePane = bareFixture.nativeElement.querySelector('.cdk-overlay-pane') as HTMLElement;
    const bareHandle = bareFixture.nativeElement.querySelector(
      '[data-sheet-handle]',
    ) as HTMLElement;

    down(bareHandle, 0);
    move(bareHandle, SHEET_DISMISS_DISTANCE_PX);
    up(bareHandle);

    expect(barePane.style.transform).toBe('');
  });
});
