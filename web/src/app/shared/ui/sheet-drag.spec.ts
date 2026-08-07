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

  function move(target: HTMLElement, clientY: number, pointerId = 1, timeStamp?: number): void {
    const event = new PointerEvent('pointermove', { pointerId, clientY, bubbles: true });
    target.dispatchEvent(timeStamp === undefined ? event : withTimeStamp(event, timeStamp));
  }

  // The release carries a position of its own, because that is where the speed is measured to — a
  // real pointerup does, and a helper that dropped it would report every gesture as ending at 0.
  function up(target: HTMLElement, clientY = 0, pointerId = 1, timeStamp?: number): void {
    const event = new PointerEvent('pointerup', { pointerId, clientY, bubbles: true });
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

  // The reason the directive defers everything to the first move. Capturing on pointerdown
  // retargets the following click to the capture element, which kills every button inside the
  // sheet whenever `(pointer: coarse)` matches while the input is a mouse — DevTools' pointer
  // emulation, verified in real Chrome. A press has to be inert.
  it('leaves an ordinary press completely untouched — no capture, no inline style', () => {
    down(handle, 0);
    up(handle);

    expect(capture.set).not.toHaveBeenCalled();
    expect(pane.style.transition).toBe('');
    expect(pane.style.transform).toBe('');
    expect(closeSpy).not.toHaveBeenCalled();
  });

  it('stays inert while the pointer only jitters below the drag threshold', () => {
    down(handle, 0);
    move(handle, 3);
    up(handle);

    expect(capture.set).not.toHaveBeenCalled();
    expect(pane.style.transform).toBe('');
  });

  it('follows the pointer once a gesture starts on the handle', () => {
    down(handle, 0);
    expect(capture.set).not.toHaveBeenCalled();

    move(handle, 40);

    // Capture is taken at the first qualifying move, not at the press before it.
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
    up(handle, SHEET_MIN_TRAVEL_PX - 1);

    expect(pane.style.transform).toBe('');
    expect(closeSpy).not.toHaveBeenCalled();
  });

  it('closes the dialog when the drag clears the distance threshold', () => {
    down(handle, 0);
    move(handle, SHEET_DISMISS_DISTANCE_PX);
    up(handle, SHEET_DISMISS_DISTANCE_PX);

    expect(closeSpy).toHaveBeenCalledTimes(1);
  });

  it('dismisses a short drag released fast enough to clear the velocity threshold', () => {
    // 24 px in 40 ms is 0.6 px/ms — over SHEET_DISMISS_VELOCITY_PX_PER_MS (0.5), and the distance
    // (SHEET_MIN_TRAVEL_PX) stays well under SHEET_DISMISS_DISTANCE_PX (96), so only the velocity
    // branch can be responsible for the close.
    down(handle, 0, 1, 0);
    move(handle, SHEET_MIN_TRAVEL_PX, 1, 0);
    up(handle, SHEET_MIN_TRAVEL_PX, 1, 40);

    expect(closeSpy).toHaveBeenCalledTimes(1);
  });

  // The defect the velocity window exists for, and the one a real thumb produced: a hasty gesture is
  // a short one, so it stands or falls on speed alone — and speed used to be averaged over the whole
  // press. Here the finger rests for half a second, then covers 40 px in 70 ms. Its tail is
  // 0.57 px/ms; spread over the press it is 0.08, under the threshold, and the sheet snaps back
  // under the hand that just flicked it away.
  it('dismisses a fast flick that follows a pause, instead of averaging the pause into it', () => {
    down(handle, 0, 1, 0);
    move(handle, 5, 1, 500);
    move(handle, 45, 1, 560);
    up(handle, 45, 1, 570);

    expect(closeSpy).toHaveBeenCalledTimes(1);
  });

  // The other half of a hasty gesture, and the reason the release position is read at all: a browser
  // coalesces pointermove and drops what it cannot deliver, so the last move that arrives can be far
  // short of where the finger actually ended. Only one move lands here, at 20 px, while the release
  // is past the distance threshold. The velocity branch cannot rescue this case — 116 px over 300 ms
  // is 0.39 px/ms, under SHEET_DISMISS_VELOCITY_PX_PER_MS — so only the distance can close it.
  it('measures the distance to the release, not to the last move the browser bothered to send', () => {
    down(handle, 0, 1, 0);
    move(handle, 20, 1, 20);
    up(handle, SHEET_DISMISS_DISTANCE_PX + 20, 1, 300);

    expect(closeSpy).toHaveBeenCalledTimes(1);
  });

  it('springs back on the same short distance released too slowly to clear the velocity threshold', () => {
    // Same 24 px, but held for a second before letting go — 0.024 px/ms, under both thresholds. This
    // is the half the window must not break: measuring the tail must not turn a finger that came to
    // rest into a flick, which is why the elapsed time runs to the release and not to the last move.
    down(handle, 0, 1, 0);
    move(handle, SHEET_MIN_TRAVEL_PX, 1, 0);
    up(handle, SHEET_MIN_TRAVEL_PX, 1, 1000);

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
    up(bareHandle, SHEET_DISMISS_DISTANCE_PX);

    expect(barePane.style.transform).toBe('');
  });
});
