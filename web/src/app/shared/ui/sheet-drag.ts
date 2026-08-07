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

  // A destroyed host never gets its pointerup/pointercancel — Angular tears down the listener along
  // with the element, it does not synthesize the event first. Without this, a dialog closed by other
  // means mid-drag (e.g. a route change) would leave the pane's transform and the pointer capture
  // dangling on an element nobody is going to touch again.
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
    this.host.nativeElement.setPointerCapture(event.pointerId);
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

    if (shouldDismiss(distance, distance / elapsedMs)) {
      this.dialogRef?.close();
      return;
    }

    pane.style.transform = '';
  }

  /** Shared by the destroy path: releases capture and leaves the pane exactly as it found it. */
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
