import { Dialog, DialogRef } from '@angular/cdk/dialog';
import { ComponentType } from '@angular/cdk/portal';

/**
 * The id `DialogShell` puts on its heading, and the only value any dialog passes to
 * `ariaLabelledBy`. A single fixed id rather than one per dialog: the CDK allows exactly one dialog
 * open at a time in this app, so the ids cannot collide — and a per-dialog id made naming a duty of
 * every *call site*, which five of the twelve had quietly skipped.
 */
export const DIALOG_TITLE_ID = 'app-dialog-title';

/** What a dialog's own `open…()` wrapper passes through. */
export interface AppDialogOptions<D> {
  data?: D;
  /**
   * Names the dialog when it has no visible heading — the confirm dialogs, whose message already
   * opens by restating the action, so a heading above it would say the same thing twice. Pass a
   * short action phrase ("Channel verlassen"), never the message itself.
   */
  ariaLabel?: string;
}

/**
 * Opens a dialog with the app's CDK chrome.
 *
 * Not called directly from pages: every dialog component exports its own thin `open…()` wrapper
 * around this, so the config that dialog needs lives next to the dialog rather than at each call
 * site. The three-line `backdropClass`/`panelClass`/`ariaLabelledBy` block used to be hand-copied
 * at twelve call sites, and the copies had already drifted apart — same reason the tab bar became
 * `TabLink`.
 */
export function openAppDialog<R, D = unknown>(
  dialog: Dialog,
  component: ComponentType<unknown>,
  options: AppDialogOptions<D> = {},
): DialogRef<R> {
  return dialog.open<R, D>(component, {
    data: options.data,
    backdropClass: 'app-dialog-backdrop',
    panelClass: 'app-dialog-panel',
    ...(options.ariaLabel ? { ariaLabel: options.ariaLabel } : { ariaLabelledBy: DIALOG_TITLE_ID }),
  });
}
