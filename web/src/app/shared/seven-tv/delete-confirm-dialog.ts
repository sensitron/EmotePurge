import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { Component, Signal, computed, inject } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { EmoteSetWarning } from '../../core/emotes/emote-admin.service';
import { pluralKey } from '../../core/i18n/plural';
import { Button } from '../ui/button';

/** Live view onto the host panel's state: the warning check finishes while the dialog is open,
 *  so the dialog reads the panel's signals instead of taking a snapshot. */
export interface DeleteConfirmDialogData {
  emotes: Signal<string[]>;
  warning: Signal<EmoteSetWarning | null>;
  warningLoading: Signal<boolean>;
}

/** Opened via cdk Dialog (S3-16) — closes with `true` when the user confirms the delete run.
 *  Focus trap, Escape, backdrop click and aria-modal come from the CDK container. */
@Component({
  selector: 'app-delete-confirm-dialog',
  imports: [Button, TranslocoPipe],
  template: `
    <div class="rounded-lg bg-slate-900 p-6 shadow-xl">
      <h2 id="delete-confirm-dialog-title" class="mb-3 text-lg font-semibold">
        {{ confirmTitleKey() | transloco: { count: data.emotes().length } }}
      </h2>
      <ul class="mb-4 max-h-48 space-y-1 overflow-y-auto text-sm text-slate-300">
        @for (emote of previewEmotes(); track emote) {
          <li>{{ emote }}</li>
        }
        @if (data.emotes().length > previewEmotes().length) {
          <li class="text-slate-400">
            {{ andMoreKey() | transloco: { count: data.emotes().length - previewEmotes().length } }}
          </li>
        }
      </ul>

      @if (data.warningLoading()) {
        <p class="mb-4 text-sm text-slate-400" role="status">
          {{ 'massDelete.checkingSharedSets' | transloco }}
        </p>
      } @else if (hasSharedSetWarning(); as warning) {
        <div
          class="mb-4 rounded-md border border-red-800 bg-red-950/50 px-3 py-2 text-sm text-red-300"
          role="alert"
        >
          <p class="font-medium">{{ 'massDelete.sharedSetWarningTitle' | transloco }}</p>
          @if (!warning.isOwnSet) {
            <p class="mt-1">{{ 'massDelete.notOwnSet' | transloco }}</p>
          }
          @if (warning.otherTrackedChannelsSharingSet.length > 0) {
            <p class="mt-1">
              {{
                'massDelete.knownAffected'
                  | transloco: { list: warning.otherTrackedChannelsSharingSet.join(', ') }
              }}
            </p>
          }
          @if (warning.otherModeratedChannelsSharingSet.length > 0) {
            <p class="mt-1">
              {{
                'massDelete.moderatedAffected'
                  | transloco: { list: warning.otherModeratedChannelsSharingSet.join(', ') }
              }}
            </p>
          }
        </div>
      } @else if (ownershipCheckUnavailable()) {
        <div
          class="mb-4 rounded-md border border-amber-700 bg-amber-950/40 px-3 py-2 text-sm text-amber-200"
          role="alert"
        >
          <p>{{ 'massDelete.ownershipCheckUnavailable' | transloco }}</p>
        </div>
      }

      <p class="mb-1 text-sm text-amber-400">
        {{ 'massDelete.irreversibleNotice' | transloco }}
      </p>
      <p class="mb-4 text-xs text-slate-400">
        {{ 'massDelete.undetectableChannelsNotice' | transloco }}
      </p>
      <div class="flex justify-end gap-2">
        <button type="button" appButton="outline" buttonSize="lg" (click)="dialogRef.close(false)">
          {{ 'common.cancel' | transloco }}
        </button>
        <button
          type="button"
          appButton="danger-solid"
          buttonSize="lg"
          class="disabled:cursor-not-allowed"
          [disabled]="data.warningLoading()"
          (click)="dialogRef.close(true)"
        >
          {{ 'massDelete.startDelete' | transloco }}
        </button>
      </div>
    </div>
  `,
})
export class DeleteConfirmDialog {
  protected readonly data = inject<DeleteConfirmDialogData>(DIALOG_DATA);
  protected readonly dialogRef = inject<DialogRef<boolean>>(DialogRef);

  protected readonly previewEmotes = computed(() => this.data.emotes().slice(0, 50));
  protected readonly confirmTitleKey = computed(() =>
    pluralKey(this.data.emotes().length, 'massDelete.confirmTitle'),
  );
  protected readonly andMoreKey = computed(() =>
    pluralKey(this.data.emotes().length - this.previewEmotes().length, 'massDelete.andMore'),
  );

  // Only surface the alarming (red) block when the check actually *ran* and found something to
  // flag — `available: false` means the check itself failed, not that the set was confirmed
  // foreign; conflating the two produced a guaranteed false alarm on every network hiccup.
  protected readonly hasSharedSetWarning = computed<EmoteSetWarning | null>(() => {
    const w = this.data.warning();
    if (!w || !w.available) {
      return null;
    }
    const flagged =
      !w.isOwnSet ||
      w.otherTrackedChannelsSharingSet.length > 0 ||
      w.otherModeratedChannelsSharingSet.length > 0;
    return flagged ? w : null;
  });

  // Separate, neutrally-worded (amber, not red) notice for "we couldn't tell" — distinct from
  // the red "we checked and it's shared" case above.
  protected readonly ownershipCheckUnavailable = computed(() => {
    const w = this.data.warning();
    return w !== null && !w.available;
  });
}
