import { DialogRef } from '@angular/cdk/dialog';
import { Directive, ElementRef, OnDestroy, inject, input } from '@angular/core';

import { shouldDismiss } from './sheet-drag-policy';

/**
 * How far a pointer has to travel before this is a drag rather than a press.
 *
 * Load-bearing, not cosmetic. Taking pointer capture on `pointerdown` retargets the following
 * `click` to the capture element, so with a mouse-type pointer every button inside the sheet stops
 * working — and `(pointer: coarse)` matches with a mouse in exactly the configuration this branch
 * is tested in (DevTools' "Emulate CSS media feature pointer: coarse"). A press that never moves
 * must therefore leave no trace at all: no capture, no inline style, nothing.
 */
const DRAG_START_THRESHOLD_PX = 4;

/**
 * How much of the gesture's tail the release speed is read from.
 *
 * Speed has to describe how the finger was moving when it left the glass, not how the gesture went
 * on average. Measured over the whole gesture, every millisecond the finger spent resting before it
 * set off divides the result — a flick that covers 40 px in 70 ms reads as 0.57 px/ms on its own and
 * as 0.08 px/ms once half a second of hesitation is counted into it. The second number misses
 * SHEET_DISMISS_VELOCITY_PX_PER_MS, the sheet springs back, and the harder someone flicks the worse
 * it gets: a hasty gesture is a short one, so it has nothing but speed to be judged on.
 *
 * A window rather than the last pair of move events, because a single frame can hold no movement at
 * all and would read as a dead stop. And it keeps the other end honest: a finger that comes to rest
 * before letting go leaves the window empty of travel, which is exactly the spring-back case.
 */
const VELOCITY_WINDOW_MS = 100;

/**
 * Drag-to-dismiss for the bottom-sheet form of a dialog.
 *
 * Applied to the shell's hull but transforming the overlay pane, because the pane is what the
 * geometry and the scrolling live on (styles.css) — moving the hull inside a scrolling pane would
 * fight that scroll rather than replace it. `closest` rather than an injected reference: the pane is
 * CDK's element, created outside the app's DOM, and there is no token for it.
 *
 * The gesture only starts on the handle or with the pane scrolled to the top. Otherwise a downward
 * drag means "scroll the content up", and taking it would make a long sheet unreadable.
 *
 * A press is recorded but not acted on: capture and the transition override wait for the first move
 * past DRAG_START_THRESHOLD_PX. Everything short of that is somebody tapping a button.
 *
 * Only `pointerdown` is a host binding. Move/up/cancel are attached to `window` for the duration of
 * a gesture and removed again afterwards — permanent host bindings for `pointermove` would notify
 * the (zoneless) scheduler on every frame of every pointer that crosses an open dialog, including
 * while the directive is switched off. `window` rather than the host element because before capture
 * is taken a finger may leave the hull mid-gesture, and after it is taken the events still bubble
 * through here.
 *
 * Dismissal goes through DialogRef.close(), so it is not a fourth way out — backdrop tap and Escape
 * are untouched, and every consumer's close handling keeps working unchanged.
 */
@Directive({
  selector: '[appSheetDrag]',
  host: {
    '(pointerdown)': 'onPointerDown($event)',
  },
})
export class SheetDrag implements OnDestroy {
  /** Off while the dialog is a centred card — there is nothing to drag it out of. */
  readonly appSheetDrag = input(false);

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly dialogRef = inject(DialogRef, { optional: true });

  // The tail of the gesture, oldest first, trimmed to VELOCITY_WINDOW_MS on every move.
  private readonly samples: { y: number; at: number }[] = [];

  private pane: HTMLElement | null = null;
  private pointerId: number | null = null;
  private startY = 0;
  private distance = 0;
  private dragging = false;

  // The host can be destroyed while its pane survives: the sheet chrome sits behind a
  // viewport-width @if, so a breakpoint crossing mid-drag (a rotation, a resize) destroys this
  // directive without the dialog closing — pointerup/pointercancel never fire because the element
  // they would land on is already gone. Without this, the pane would be left at whatever transform
  // the drag had reached, the host would still hold pointer capture nobody will use again, and the
  // window listeners would outlive the directive that owns them.
  ngOnDestroy(): void {
    const pane = this.pane;
    const pointerId = this.pointerId;
    const wasDragging = this.dragging;

    this.forgetGesture();

    if (!wasDragging || !pane || pointerId === null) {
      return;
    }
    this.releaseCapture(pointerId);
    pane.style.transition = '';
    pane.style.transform = '';
  }

  protected onPointerDown(event: PointerEvent): void {
    if (!this.appSheetDrag() || this.pointerId !== null) {
      return;
    }

    const pane = this.host.nativeElement.closest<HTMLElement>('.cdk-overlay-pane');
    if (!pane) {
      return;
    }

    const target = event.target as HTMLElement | null;
    const onHandle = target?.closest('[data-sheet-handle]') != null;
    if (!onHandle && pane.scrollTop > 0) {
      return;
    }

    // Eligibility and origin only. Nothing here may be observable from the outside — see
    // DRAG_START_THRESHOLD_PX.
    this.pane = pane;
    this.pointerId = event.pointerId;
    this.startY = event.clientY;
    this.distance = 0;
    this.dragging = false;
    this.samples.length = 0;
    this.samples.push({ y: event.clientY, at: event.timeStamp });
    window.addEventListener('pointermove', this.onMove);
    window.addEventListener('pointerup', this.onEnd);
    window.addEventListener('pointercancel', this.onEnd);
  }

  private onPointerMove(event: PointerEvent): void {
    const pane = this.pane;
    if (this.pointerId !== event.pointerId || !pane) {
      return;
    }

    this.track(event.clientY, event.timeStamp);

    const travelled = event.clientY - this.startY;
    if (!this.dragging) {
      // Measured on the absolute travel: an upward flick past the threshold is still a drag, it
      // just has nowhere to go — the clamp below turns it into a docked sheet, not a dead one.
      if (Math.abs(travelled) < DRAG_START_THRESHOLD_PX) {
        return;
      }
      this.beginDrag(pane, event.pointerId);
    }

    // No resistance upwards: a sheet that follows the finger past its docked edge reads as broken.
    this.distance = Math.max(0, travelled);
    pane.style.transform = `translateY(${this.distance}px)`;
  }

  private onPointerEnd(event: PointerEvent): void {
    const pane = this.pane;
    if (this.pointerId !== event.pointerId || !pane) {
      return;
    }

    const wasDragging = this.dragging;
    // Measured to the release, not to the last move that arrived. A browser coalesces pointermove
    // and drops what it cannot deliver in time, so the faster the gesture the further the tracked
    // position lags behind the finger — and a fast gesture is a short one, judged against the very
    // threshold those missing pixels would have carried it over. The release is the last and truest
    // sample of the gesture; there is no reason to decide on an older one.
    const distance = wasDragging ? Math.max(0, event.clientY - this.startY) : this.distance;
    const velocity = this.releaseVelocity(event);

    this.forgetGesture();

    // An ordinary press. Capture was never taken and no style was ever written, so there is nothing
    // to undo — and, decisively, the click that follows reaches the button it started on.
    if (!wasDragging) {
      return;
    }

    // Capture is released implicitly by pointerup/pointercancel; only the transition override is
    // ours to undo.
    pane.style.transition = '';

    // Only skip the spring-back when a close was actually issued: a pane inside anything that isn't
    // a Dialog (no DialogRef to inject) would otherwise be parked at an inline transform with nothing
    // in flight to ever clear it, and an inline transform on a successful close would still outrank
    // any class-based exit animation the sheet chrome adds later.
    if (shouldDismiss(distance, velocity) && this.dialogRef) {
      this.dialogRef.close();
      return;
    }

    pane.style.transform = '';
  }

  /** Promotes the recorded press to a real drag — the first point at which anything is observable. */
  private beginDrag(pane: HTMLElement, pointerId: number): void {
    this.dragging = true;
    // The spring-back transition would otherwise animate every move event.
    pane.style.transition = 'none';
    try {
      this.host.nativeElement.setPointerCapture(pointerId);
    } catch {
      // Not fatal, deliberately swallowed: a pointer that refuses capture still delivers move/up
      // events here via the window listeners, it just stops redirecting them once the finger leaves
      // the element's bounds. The alternative — bailing out here — would abandon a gesture that has
      // already recorded its start position for no gain.
    }
  }

  /** Records a position and drops whatever has aged out of the window behind it. */
  private track(y: number, at: number): void {
    this.samples.push({ y, at });
    while (this.samples.length > 1 && at - this.samples[0].at > VELOCITY_WINDOW_MS) {
      this.samples.shift();
    }
  }

  /**
   * Downward speed in px/ms at the moment of release, from the oldest sample still in the window to
   * the release itself. Elapsed time is measured to the release rather than to the last move, so a
   * finger that stopped and then let go reads as stopped instead of keeping the speed it once had.
   */
  private releaseVelocity(event: PointerEvent): number {
    const oldest = this.samples[0];
    if (!oldest) {
      return 0;
    }
    // Guarded so a same-timestamp release cannot produce Infinity, and a clock that jumped backwards
    // cannot turn a downward flick into an upward one.
    const elapsedMs = event.timeStamp - oldest.at;
    return elapsedMs > 0 ? (event.clientY - oldest.y) / elapsedMs : 0;
  }

  /** Detaches the gesture's listeners and forgets its state. Touches no DOM of the sheet itself. */
  private forgetGesture(): void {
    if (this.pointerId === null) {
      return;
    }
    window.removeEventListener('pointermove', this.onMove);
    window.removeEventListener('pointerup', this.onEnd);
    window.removeEventListener('pointercancel', this.onEnd);
    this.pane = null;
    this.pointerId = null;
    this.distance = 0;
    this.dragging = false;
    this.samples.length = 0;
  }

  private releaseCapture(pointerId: number): void {
    // The pointer may already be gone (gesture ended, element mid-detach) — releasing then throws
    // a DOMException that means nothing here, there is nothing left to release.
    try {
      this.host.nativeElement.releasePointerCapture(pointerId);
    } catch {
      // Ignored — see above.
    }
  }

  // Stable references so the listeners a gesture adds are the ones it can remove again. Last in the
  // class because member-ordering counts a bound arrow field as a private method definition.
  private readonly onMove = (event: PointerEvent): void => this.onPointerMove(event);
  private readonly onEnd = (event: PointerEvent): void => this.onPointerEnd(event);
}
