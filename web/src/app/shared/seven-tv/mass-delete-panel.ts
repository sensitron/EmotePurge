import { Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { EmoteAdminService, EmoteSetWarning } from '../../core/emotes/emote-admin.service';
import { DeleteQueueEmote, SevenTvDeleteService } from '../../core/seven-tv/seven-tv-delete.service';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { DeleteConfirmDialog } from './delete-confirm-dialog';
import { DeleteProgressPanel } from './delete-progress-panel';
import { SevenTvTokenInput } from './seven-tv-token-input';

export interface DeletableEmote {
  emoteId: string;
  sevenTvEmoteId: string;
  name: string;
}

/**
 * One reusable delete engine, rendered as two separate instances (Usage-Stats page and
 * Voting-Results page) — each instance owns its host page's local selection, but both talk to
 * the same singleton SevenTvDeleteService/SevenTvTokenService underneath.
 */
@Component({
  selector: 'app-mass-delete-panel',
  imports: [DeleteConfirmDialog, DeleteProgressPanel, SevenTvTokenInput, TranslocoPipe],
  template: `
    <div class="flex flex-col gap-3">
      <button
        type="button"
        class="self-start rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white transition hover:bg-red-500 disabled:cursor-not-allowed disabled:opacity-50"
        [disabled]="selectedEmotes().length === 0 || deleteService.isRunning()"
        (click)="openConfirm()"
      >
        {{ 'massDelete.deleteButton' | transloco: { count: selectedEmotes().length } }}
      </button>

      @if (deleteService.isRunning() || deleteService.queue().length > 0) {
        <app-delete-progress-panel
          [items]="deleteService.queue()"
          [isRunning]="deleteService.isRunning()"
          (cancelled)="deleteService.cancel()"
        />
      }

      @if (showConfirm()) {
        @if (tokenService.hasToken()) {
          <app-delete-confirm-dialog
            [emotes]="selectedEmoteNames()"
            [warning]="setWarning()"
            [warningLoading]="warningLoading()"
            (confirmed)="startDelete()"
            (cancelled)="showConfirm.set(false)"
          />
        } @else {
          <div class="fixed inset-0 z-50 flex items-center justify-center bg-black/60 px-4" role="dialog" aria-modal="true">
            <div class="w-full max-w-md rounded-lg bg-slate-900 p-6 shadow-xl">
              <app-seven-tv-token-input />
              <button type="button" class="mt-4 text-sm text-slate-400 hover:underline" (click)="showConfirm.set(false)">
                {{ 'common.cancel' | transloco }}
              </button>
            </div>
          </div>
        }
      }
    </div>
  `,
})
export class MassDeletePanel {
  readonly setId = input.required<string>();
  readonly channelName = input.required<string>();
  readonly selectedEmotes = input.required<DeletableEmote[]>();
  readonly deleted = output<string[]>();

  protected readonly tokenService = inject(SevenTvTokenService);
  protected readonly deleteService = inject(SevenTvDeleteService);
  private readonly emoteAdminService = inject(EmoteAdminService);

  protected readonly showConfirm = signal(false);
  protected readonly setWarning = signal<EmoteSetWarning | null>(null);
  protected readonly warningLoading = signal(false);
  protected readonly selectedEmoteNames = computed(() => this.selectedEmotes().map((emote) => emote.name));

  constructor() {
    // Emits `deleted` once per run, right when the queue settles (isRunning flips back to
    // false), so the host page can optimistically drop finished emotes without a refetch.
    let wasRunning = false;
    effect(() => {
      const running = this.deleteService.isRunning();
      if (wasRunning && !running) {
        const doneIds = this.deleteService
          .queue()
          .filter((item) => item.status === 'done')
          .map((item) => item.emoteId);
        if (doneIds.length > 0) {
          this.deleted.emit(doneIds);
        }
      }
      wasRunning = running;
    });
  }

  protected openConfirm(): void {
    this.showConfirm.set(true);
    this.setWarning.set(null);
    this.warningLoading.set(true);

    this.emoteAdminService.getSetWarning(this.channelName()).subscribe({
      next: (warning) => {
        this.setWarning.set(warning);
        this.warningLoading.set(false);
      },
      error: () => {
        // Conservative fallback: an unresolved check must not read as "all clear".
        this.setWarning.set({
          available: false,
          isOwnSet: false,
          otherTrackedChannelsSharingSet: [],
          otherModeratedChannelsSharingSet: [],
        });
        this.warningLoading.set(false);
      },
    });
  }

  protected startDelete(): void {
    this.showConfirm.set(false);
    const emotes: DeleteQueueEmote[] = this.selectedEmotes().map((emote) => ({
      emoteId: emote.emoteId,
      sevenTvEmoteId: emote.sevenTvEmoteId,
      name: emote.name,
    }));
    this.deleteService.startDelete(this.setId(), this.channelName(), emotes);
  }
}
