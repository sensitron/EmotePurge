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
import { NoticeBanner } from '../ui/notice-banner';
import { SkeletonRows } from '../ui/skeleton-rows';
import { ForeignEmoteGrid } from './foreign-emote-grid';

/**
 * What this step yields once the user has picked emotes to bring over — a self-contained payload,
 * not an `ImportSource`/`ImportOrigin`. Those types (`core/seven-tv/import-source.ts`) carry the
 * third `'seventv-channel'` variant, and `buildForeignImportSource` turns this result into one; the
 * step stays unaware of that union so it does not have to change again the day it grows a fourth
 * member.
 */
export interface ForeignChannelImportResult {
  /** Normalized (Regel 9) — what the resolved channel is actually called. */
  channelName: string;
  sevenTvUserId: string;
  emoteSetId: string;
  /** Only the rows the user marked, in the grid's selection order. Never the full set — the whole
   *  point of this step (spec Falle F4, the user's "NICHT alle direkt übernehmen"). */
  rows: ForeignEmoteRow[];
}

type LoadState =
  | { status: 'idle' }
  | { status: 'loading' }
  | { status: 'error'; error: HttpErrorResponse }
  | { status: 'loaded'; response: ForeignEmoteSetResponse };

/**
 * The "Aus einem Kanal" branch of the one import dialog (spec §7): a logged-in user types an
 * arbitrary Twitch login, loads that channel's active 7TV set — no role in that channel required —
 * and marks individual emotes in the `ForeignEmoteGrid` below.
 *
 * It reports its state rather than closing anything: {@link result} is `null` until a set is loaded
 * *and* something is selected, and `ImportSourceDialog` renders the "Weiter" button of the shared
 * action row against it (§7 keeps the action row with the dialog, not with the step).
 *
 * A fresh channel query re-renders the `@case ('loaded')` branch from scratch (`load()` moves the
 * state back through `'loading'`), which unmounts and remounts `ForeignEmoteGrid` — a new
 * `ListSelection` instance, i.e. a new query always starts unselected. That is deliberate: a
 * selection made against one channel's 7TV ids has no honest meaning carried over to a different
 * channel's set. The same happens on "neu laden" (E3's cache-bypass).
 */
@Component({
  selector: 'app-foreign-channel-step',
  imports: [
    Button,
    ForeignEmoteGrid,
    NoticeBanner,
    ReactiveFormsModule,
    SkeletonRows,
    TranslocoPipe,
  ],
  template: `
    <!-- The 24rem cap is the field's own business, not the pane's: the pane is wide for the emote
         grid below and stays that width across every step (§7.3, no layout jumps), but a normalized
         Twitch login is a dozen characters and an input stretched across 72rem reads as a mistake.
         It lands the input at roughly the width it had in the 28rem pane this dialog grew out of,
         and above the app's own w-40/w-44 filter fields. -->
    <form class="flex max-w-sm flex-col gap-1" (submit)="onFormSubmit($event)">
      <!-- A visible label, not just a placeholder: this is a form field in a dialog body, not a
           filter toolbar, so the design language's label duty (§5.2) applies in full. -->
      <label class="text-sm text-fg-secondary" [for]="channelInputId">
        {{ 'import.foreignChannel.channelLabel' | transloco }}
      </label>
      <div class="flex flex-wrap gap-2">
        <input
          type="text"
          [id]="channelInputId"
          [formControl]="channelNameControl"
          [placeholder]="'import.foreignChannel.placeholder' | transloco"
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
      </div>
      @if (channelNameControl.invalid && channelNameControl.touched) {
        <p id="foreign-channel-name-error" class="text-sm text-danger-fg">
          {{ 'import.foreignChannel.invalidChannelName' | transloco }}
        </p>
      }
    </form>

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
  `,
  host: { class: 'flex min-h-0 flex-col gap-3' },
})
export class ForeignChannelStep {
  private readonly emoteSetService = inject(ForeignEmoteSetService);

  // Validates the *normalized* value (Regel 9) — same reasoning and the same validator the admin
  // "join channel" form uses (`admin-channels-page.ts`): an admin/user can paste "HandOfBlood" the
  // way Twitch displays it, since the server normalizes before matching its own lower-case pattern.
  protected readonly channelNameControl = new FormControl('', {
    nonNullable: true,
    validators: [channelNameValidator],
  });

  protected readonly channelInputId = 'foreign-channel-name';

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

  /**
   * The step's whole outward contract: `null` while there is nothing to carry forward, the payload
   * as soon as a loaded set has at least one marked emote. A signal rather than a method so the
   * dialog's own `computed()` over a `viewChild` reacts to it (Regel 14).
   */
  readonly result = computed<ForeignChannelImportResult | null>(() => {
    const response = this.loadedResponse();
    const rows = this.selectedRows();
    if (response === null || rows.length === 0) {
      return null;
    }
    return {
      channelName: response.channelName,
      sevenTvUserId: response.sevenTvUserId,
      emoteSetId: response.emoteSetId,
      rows,
    };
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
