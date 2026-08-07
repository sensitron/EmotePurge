import { DialogRef } from '@angular/cdk/dialog';
import { Directive, ElementRef, OnDestroy, inject, input } from '@angular/core';

import { shouldDismiss } from './sheet-drag-policy';

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
 * Dismissal goes through DialogRef.close(), so it is not a fourth way out — backdrop tap and Escape
 * are untouched, and every consumer's close handling keeps working unchanged.
 */
@Directive({
  selector: '[appSheetDrag]',
  host: {
    '(pointerdown)': 'onPointerDown($event)',
    '(pointermove)': 'onPointerMove($event)',
    '(pointerup)': 'onPointerEnd($event)',
    '(pointercancel)': 'onPointerEnd($event)',
  },
})
export class SheetDrag implements OnDestroy {
  /** Off while the dialog is a centred card — there is nothing to drag it out of. */
  readonly appSheetDrag = input(false);

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly dialogRef = inject(DialogRef, { optional: true });

  private pane: HTMLElement | null = null;
  private pointerId: number | null = null;
  private startY = 0;
  private startedAt = 0;
  private distance = 0;

  // The host can be destroyed while its pane survives: the sheet chrome sits behind a
  // viewport-width @if, so a breakpoint crossing mid-drag (a rotation, a resize) destroys this
  // directive without the dialog closing — pointerup/pointercancel never fire because the element
  // they would land on is already gone. Without this, the pane would be left at whatever transform
  // the drag had reached, and the host would still hold pointer capture nobody will use again.
  ngOnDestroy(): void {
    this.endGesture();
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

    this.pane = pane;
    this.pointerId = event.pointerId;
    this.startY = event.clientY;
    this.startedAt = event.timeStamp;
    this.distance = 0;
    // The spring-back transition would otherwise animate every move event.
    pane.style.transition = 'none';
    try {
      this.host.nativeElement.setPointerCapture(event.pointerId);
    } catch {
      // Not fatal, deliberately swallowed: a pointer that refuses capture still delivers move/up
      // events here via ordinary bubbling, it just stops redirecting them once the finger leaves the
      // element's bounds. The alternative — bailing out here — would abandon a gesture that has
      // already recorded its start position for no gain.
    }
  }

  protected onPointerMove(event: PointerEvent): void {
    if (this.pointerId !== event.pointerId || !this.pane) {
      return;
    }

    // No resistance upwards: a sheet that follows the finger past its docked edge reads as broken.
    this.distance = Math.max(0, event.clientY - this.startY);
    this.pane.style.transform = `translateY(${this.distance}px)`;
  }

  protected onPointerEnd(event: PointerEvent): void {
    if (this.pointerId !== event.pointerId || !this.pane) {
      return;
    }

    const pane = this.pane;
    const distance = this.distance;
    // Guarded against 0 so a same-timestamp release cannot produce Infinity.
    const elapsedMs = Math.max(1, event.timeStamp - this.startedAt);

    this.pane = null;
    this.pointerId = null;
    pane.style.transition = '';

    // Only skip the spring-back when a close was actually issued: a pane inside anything that isn't
    // a Dialog (no DialogRef to inject) would otherwise be parked at an inline transform with nothing
    // in flight to ever clear it, and an inline transform on a successful close would still outrank
    // any class-based exit animation the sheet chrome adds later.
    if (shouldDismiss(distance, distance / elapsedMs) && this.dialogRef) {
      this.dialogRef.close();
      return;
    }

    pane.style.transform = '';
  }

  /** Releases pointer capture and puts the pane back to its undragged state. */
  private endGesture(): void {
    if (this.pointerId === null || !this.pane) {
      return;
    }

    // The pointer may already be gone (gesture ended, element mid-detach) — releasing then throws
    // a DOMException that means nothing here, there is nothing left to release.
    try {
      this.host.nativeElement.releasePointerCapture(this.pointerId);
    } catch {
      // Ignored — see above.
    }

    this.pane.style.transition = '';
    this.pane.style.transform = '';
    this.pane = null;
    this.pointerId = null;
  }
}
