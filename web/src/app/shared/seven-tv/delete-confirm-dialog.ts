import { Component, computed, input, output } from '@angular/core';
import { A11yModule } from '@angular/cdk/a11y';
import { TranslocoPipe } from '@jsverse/transloco';

import { EmoteSetWarning } from '../../core/emotes/emote-admin.service';
import { pluralKey } from '../../core/i18n/plural';

@Component({
  selector: 'app-delete-confirm-dialog',
  imports: [A11yModule, TranslocoPipe],
  template: `
    <div
      class="fixed inset-0 z-50 flex items-center justify-center bg-black/60 px-4"
      role="dialog"
      aria-modal="true"
      aria-labelledby="delete-confirm-dialog-title"
      tabindex="-1"
      cdkTrapFocus
      cdkTrapFocusAutoCapture
      (keydown.escape)="cancelled.emit()"
    >
      <div class="w-full max-w-md rounded-lg bg-slate-900 p-6 shadow-xl">
        <h2 id="delete-confirm-dialog-title" class="mb-3 text-lg font-medium">
          {{ confirmTitleKey() | transloco: { count: emotes().length } }}
        </h2>
        <ul class="mb-4 max-h-48 space-y-1 overflow-y-auto text-sm text-slate-300">
          @for (emote of previewEmotes(); track emote) {
            <li>{{ emote }}</li>
          }
          @if (emotes().length > previewEmotes().length) {
            <li class="text-slate-400">
              {{ andMoreKey() | transloco: { count: emotes().length - previewEmotes().length } }}
            </li>
          }
        </ul>

        @if (warningLoading()) {
          <p class="mb-4 text-sm text-slate-400" role="status">{{ 'massDelete.checkingSharedSets' | transloco }}</p>
        } @else if (hasSharedSetWarning(); as warning) {
          <div class="mb-4 rounded-md border border-red-800 bg-red-950/50 px-3 py-2 text-sm text-red-300" role="alert">
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
          <button
            type="button"
            class="rounded-md border border-slate-700 px-4 py-2 text-sm text-slate-300 transition hover:bg-slate-800"
            cdkFocusInitial
            (click)="cancelled.emit()"
          >
            {{ 'common.cancel' | transloco }}
          </button>
          <button
            type="button"
            class="rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white transition hover:bg-red-500 disabled:cursor-not-allowed disabled:opacity-50"
            [disabled]="warningLoading()"
            (click)="confirmed.emit()"
          >
            {{ 'massDelete.startDelete' | transloco }}
          </button>
        </div>
      </div>
    </div>
  `,
})
export class DeleteConfirmDialog {
  readonly emotes = input.required<string[]>();
  readonly warning = input<EmoteSetWarning | null>(null);
  readonly warningLoading = input(false);
  readonly confirmed = output<void>();
  readonly cancelled = output<void>();

  protected readonly previewEmotes = computed(() => this.emotes().slice(0, 50));
  protected readonly confirmTitleKey = computed(() => pluralKey(this.emotes().length, 'massDelete.confirmTitle'));
  protected readonly andMoreKey = computed(() =>
    pluralKey(this.emotes().length - this.previewEmotes().length, 'massDelete.andMore'),
  );

  // Only surface the alarming (red) block when the check actually *ran* and found something to
  // flag — `available: false` means the check itself failed, not that the set was confirmed
  // foreign; conflating the two produced a guaranteed false alarm on every network hiccup.
  protected readonly hasSharedSetWarning = computed<EmoteSetWarning | null>(() => {
    const w = this.warning();
    if (!w || !w.available) {
      return null;
    }
    const flagged = !w.isOwnSet || w.otherTrackedChannelsSharingSet.length > 0 || w.otherModeratedChannelsSharingSet.length > 0;
    return flagged ? w : null;
  });

  // Separate, neutrally-worded (amber, not red) notice for "we couldn't tell" — distinct from
  // the red "we checked and it's shared" case above.
  protected readonly ownershipCheckUnavailable = computed(
    () => this.warning() !== null && !this.warning()!.available,
  );
}
