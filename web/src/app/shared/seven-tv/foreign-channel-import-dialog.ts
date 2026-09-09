import { Dialog, DialogRef } from '@angular/cdk/dialog';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { TranslocoPipe } from '@jsverse/transloco';

import { channelNameValidator, normalizeChannelName } from '../../core/channels/channel-name';
import { apiErrorTranslationKey } from '../../core/i18n/api-error';
import {
  ForeignEmoteRow,
  ForeignEmoteSetResponse,
} from '../../core/seven-tv/foreign-emote-set.model';
import { ForeignEmoteSetService } from '../../core/seven-tv/foreign-emote-set.service';
import { Button } from '../ui/button';
import { openAppDialog } from '../ui/dialog';
import { DialogShell } from '../ui/dialog-shell';
import { NoticeBanner } from '../ui/notice-banner';
import { SkeletonRows } from '../ui/skeleton-rows';
import { ForeignEmoteGrid } from './foreign-emote-grid';

/**
 * What this dialog closes with once the user has picked emotes to bring over — a self-contained
 * payload, not an `ImportSource`/`ImportOrigin`. Those types (`core/seven-tv/import-source.ts`) get
 * their third `'seventv-channel'` variant from T4, which also builds the actual `ImportSource` out
 * of this result (spec F6, §7, Dateireferenz) — this dialog stays unaware of that union so it does
 * not have to change again the day it grows a fourth member.
 */
export interface ForeignChannelImportResult {
  /** Normalized (Regel 9) — what the resolved channel is actually called. */
  channelName: string;
  sevenTvUserId: string;
  emoteSetId: string;
  /** Only the rows the user marked, in the grid's selection order. Never the full set — the whole
   *  point of this dialog (spec Falle F4, the user's "NICHT alle direkt übernehmen"). */
  rows: ForeignEmoteRow[];
}

type LoadState =
  | { status: 'idle' }
  | { status: 'loading' }
  | { status: 'error'; error: HttpErrorResponse }
  | { status: 'loaded'; response: ForeignEmoteSetResponse };

/**
 * The picker half of the third import source (spec §7, "Kanalname eingeben, Set laden"): a
 * eingelogged Nutzer types an arbitrary Twitch login, loads that channel's active 7TV set — no role
 * in that channel required — and marks individual emotes in the `ForeignEmoteGrid` below. Closes
 * with a {@link ForeignChannelImportResult}, or `undefined` on cancel/Escape/backdrop, the same
 * contract every other dialog in the app follows.
 *
 * Deliberately its own dialog rather than a step bolted onto `ImportTargetDialog`/`FileImportDialog`
 * — those pick *where things go* or *read a file*, this one *resolves a foreign source*, and mixing
 * the concerns would have made every one of the three harder to reason about alone. T4 wires this in
 * next to the other two entry points; this dialog does not know how it gets opened.
 *
 * A fresh channel query re-renders the `@case ('loaded')` branch from scratch (`load()` moves the
 * state back through `'loading'`), which unmounts and remounts `ForeignEmoteGrid` — a new
 * `ListSelection` instance, i.e. a new query always starts unselected. That is deliberate: a
 * selection made against one channel's 7TV ids has no honest meaning carried over to a different
 * channel's set. The same happens on "neu laden" (E3's cache-bypass) for simplicity; nothing in the
 * spec asks a refresh to preserve the in-progress selection.
 */
@Component({
  selector: 'app-foreign-channel-import-dialog',
  imports: [
    Button,
    DialogShell,
    ForeignEmoteGrid,
    NoticeBanner,
    ReactiveFormsModule,
    SkeletonRows,
    TranslocoPipe,
  ],
  template: `
    <app-dialog-shell [dialogTitle]="'import.foreignChannel.title' | transloco">
      <form class="flex flex-wrap gap-2" (submit)="onFormSubmit($event)">
        <input
          type="text"
          [formControl]="channelNameControl"
          [placeholder]="'import.foreignChannel.placeholder' | transloco"
          [attr.aria-label]="'import.foreignChannel.channelLabel' | transloco"
          [attr.aria-invalid]="
            channelNameControl.invalid && channelNameControl.touched ? 'true' : null
          "
          [attr.aria-describedby]="
            channelNameControl.invalid && channelNameControl.touched
              ? 'foreign-channel-name-error'
              : null
          "
          class="app-input flex-1"
        />
        <button
          type="submit"
          appButton="outline"
          buttonSize="lg"
          [disabled]="state().status === 'loading'"
        >
          {{ 'import.foreignChannel.load' | transloco }}
        </button>
      </form>
      @if (channelNameControl.invalid && channelNameControl.touched) {
        <p id="foreign-channel-name-error" class="text-sm text-danger-fg">
          {{ 'import.foreignChannel.invalidChannelName' | transloco }}
        </p>
      }

      @switch (state().status) {
        @case ('loading') {
          <app-skeleton-rows [count]="3" />
        }
        @case ('error') {
          <app-notice-banner variant="error">
            {{ errorMessageKey() | transloco }}
            <button notice-action type="button" appButton="outline" (click)="submit()">
              {{ 'import.foreignChannel.retry' | transloco }}
            </button>
          </app-notice-banner>
        }
        @case ('loaded') {
          @if (loadedResponse(); as response) {
            <div class="flex items-center justify-between gap-2">
              <span class="text-xs text-fg-muted">#{{ response.channelName }}</span>
              <button type="button" appButton="neutral" (click)="reload()">
                {{ 'import.foreignChannel.reload' | transloco }}
              </button>
            </div>
            <app-foreign-emote-grid
              [emotes]="response.emotes"
              [truncated]="response.truncated"
              [totalCount]="response.totalCount"
              (selectionChange)="onSelectionChange($event)"
            />
          }
        }
      }

      <button
        dialog-actions
        type="button"
        appButton="outline"
        buttonSize="lg"
        (click)="dialogRef.close()"
      >
        {{ 'common.cancel' | transloco }}
      </button>
      @if (state().status === 'loaded') {
        <button
          dialog-actions
          type="button"
          appButton="primary"
          buttonSize="lg"
          [disabled]="selectedRows().length === 0"
          (click)="continue()"
        >
          {{ 'import.foreignChannel.continue' | transloco }}
        </button>
      }
    </app-dialog-shell>
  `,
})
export class ForeignChannelImportDialog {
  protected readonly dialogRef =
    inject<DialogRef<ForeignChannelImportResult | undefined>>(DialogRef);

  private readonly emoteSetService = inject(ForeignEmoteSetService);

  // Validates the *normalized* value (Regel 9) — same reasoning and the same validator the admin
  // "join channel" form uses (`admin-channels-page.ts`): an admin/user can paste "HandOfBlood" the
  // way Twitch displays it, since the server normalizes before matching its own lower-case pattern.
  protected readonly channelNameControl = new FormControl('', {
    nonNullable: true,
    validators: [channelNameValidator],
  });

  protected readonly state = signal<LoadState>({ status: 'idle' });
  protected readonly selectedRows = signal<ForeignEmoteRow[]>([]);

  protected readonly loadedResponse = computed<ForeignEmoteSetResponse | null>(() => {
    const current = this.state();
    return current.status === 'loaded' ? current.response : null;
  });

  protected readonly errorMessageKey = computed(() => {
    const current = this.state();
    return current.status === 'error' ? apiErrorTranslationKey(current.error) : null;
  });

  protected onFormSubmit(event: Event): void {
    event.preventDefault();
    this.submit();
  }

  protected submit(): void {
    if (this.channelNameControl.invalid) {
      this.channelNameControl.markAsTouched();
      return;
    }
    this.load(false);
  }

  protected reload(): void {
    this.load(true);
  }

  protected onSelectionChange(rows: ForeignEmoteRow[]): void {
    this.selectedRows.set(rows);
  }

  protected continue(): void {
    const response = this.loadedResponse();
    if (response === null || this.selectedRows().length === 0) {
      return;
    }
    this.dialogRef.close({
      channelName: response.channelName,
      sevenTvUserId: response.sevenTvUserId,
      emoteSetId: response.emoteSetId,
      rows: this.selectedRows(),
    });
  }

  private load(refresh: boolean): void {
    const channelName = normalizeChannelName(this.channelNameControl.value);
    this.state.set({ status: 'loading' });
    this.selectedRows.set([]);
    this.emoteSetService.load(channelName, { refresh }).subscribe({
      next: (response) => this.state.set({ status: 'loaded', response }),
      error: (error: HttpErrorResponse) => this.state.set({ status: 'error', error }),
    });
  }
}

export function openForeignChannelImportDialog(
  dialog: Dialog,
): DialogRef<ForeignChannelImportResult | undefined> {
  return openAppDialog<ForeignChannelImportResult | undefined>(dialog, ForeignChannelImportDialog);
}
