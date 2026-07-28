import { Component, computed, input, output } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { EmoteSetWarning } from '../../core/emotes/emote-admin.service';
import { pluralKey } from '../../core/i18n/plural';

@Component({
  selector: 'app-delete-confirm-dialog',
  imports: [TranslocoPipe],
  template: `
    <div class="fixed inset-0 z-50 flex items-center justify-center bg-black/60 px-4" role="dialog" aria-modal="true">
      <div class="w-full max-w-md rounded-lg bg-slate-900 p-6 shadow-xl">
        <h2 class="mb-3 text-lg font-medium">
          {{ confirmTitleKey() | transloco: { count: emotes().length } }}
        </h2>
        <ul class="mb-4 max-h-48 space-y-1 overflow-y-auto text-sm text-slate-300">
          @for (emote of previewEmotes(); track emote) {
            <li>{{ emote }}</li>
          }
          @if (emotes().length > previewEmotes().length) {
            <li class="text-slate-500">
              {{ andMoreKey() | transloco: { count: emotes().length - previewEmotes().length } }}
            </li>
          }
        </ul>

        @if (warningLoading()) {
          <p class="mb-4 text-sm text-slate-500">{{ 'massDelete.checkingSharedSets' | transloco }}</p>
        } @else if (hasSharedSetWarning(); as warning) {
          <div class="mb-4 rounded-md border border-red-800 bg-red-950/50 px-3 py-2 text-sm text-red-300">
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
        }

        <p class="mb-1 text-sm text-amber-400">
          {{ 'massDelete.irreversibleNotice' | transloco }}
        </p>
        <p class="mb-4 text-xs text-slate-500">
          {{ 'massDelete.undetectableChannelsNotice' | transloco }}
        </p>
        <div class="flex justify-end gap-2">
          <button
            type="button"
            class="rounded-md border border-slate-700 px-4 py-2 text-sm text-slate-300 transition hover:bg-slate-800"
            (click)="cancelled.emit()"
          >
            {{ 'common.cancel' | transloco }}
          </button>
          <button
            type="button"
            class="rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white transition hover:bg-red-500"
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

  // Only surface the alarming block when there's actually something to flag — a resolved warning
  // with an own, unshared set is the common, unremarkable case.
  protected readonly hasSharedSetWarning = computed<EmoteSetWarning | null>(() => {
    const w = this.warning();
    if (!w) {
      return null;
    }
    const flagged = !w.isOwnSet || w.otherTrackedChannelsSharingSet.length > 0 || w.otherModeratedChannelsSharingSet.length > 0;
    return flagged ? w : null;
  });
}
