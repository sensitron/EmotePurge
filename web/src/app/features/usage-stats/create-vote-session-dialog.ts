import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import {
  AbstractControl,
  FormControl,
  ReactiveFormsModule,
  ValidationErrors,
} from '@angular/forms';
import { TranslocoPipe } from '@jsverse/transloco';

import { apiErrorTranslationKey } from '../../core/i18n/api-error';
import { AllowedRoles, VoteSessionSummary } from '../../core/voting/vote-session.model';
import { VoteSessionService } from '../../core/voting/vote-session.service';
import { DateTimePicker } from '../../shared/datetime/datetime-picker';
import { Button } from '../../shared/ui/button';
import { NoticeBanner } from '../../shared/ui/notice-banner';

// Same rule as the inline create form on VoteSessionListPage (whitespace-only titles are empty).
function requiredTrimmed(control: AbstractControl<string>): ValidationErrors | null {
  return control.value?.trim().length > 0 ? null : { required: true };
}

function toLocalDateTimeInputValue(date: Date): string {
  const pad = (n: number) => n.toString().padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

export interface CreateVoteSessionDialogData {
  channelName: string;
  // The ballot, captured at open time — the host page clears its selection on channel/range
  // changes, so the dialog must not read it lazily.
  emoteIds: string[];
  // ISO date (YYYY-MM-DD): the from-date of the range filter active when the dialog was opened.
  // Prefills the "count usage from" picker so the session's usage figures cover the same window
  // as the numbers that informed the selection.
  usageFromDate: string;
}

/**
 * "Zur Abstimmung stellen" from a usage-stats selection: the same three fields as the inline
 * create form on the voting list page (deliberately duplicated — the form is three existing
 * primitives, and a shared building block would couple the two features for less code than it
 * saves), plus the fixed ballot from the selection. Closes with the created session on success.
 */
@Component({
  selector: 'app-create-vote-session-dialog',
  imports: [Button, DateTimePicker, NoticeBanner, ReactiveFormsModule, TranslocoPipe],
  template: `
    <div class="flex w-[28rem] max-w-[90vw] flex-col gap-3 rounded-lg bg-slate-900 p-6 shadow-xl">
      <h2 id="create-vote-session-title" class="text-lg font-semibold">
        {{ 'voting.create.dialogTitle' | transloco }}
      </h2>
      <p class="text-sm text-slate-400">
        {{ 'voting.create.selectedCount' | transloco: { count: data.emoteIds.length } }}
      </p>
      <input
        id="create-vote-session-title-input"
        type="text"
        [formControl]="titleControl"
        [placeholder]="'voting.list.titlePlaceholder' | transloco"
        [attr.aria-label]="'voting.list.titlePlaceholder' | transloco"
        [attr.aria-invalid]="titleControl.invalid && titleControl.touched ? 'true' : null"
        [attr.aria-describedby]="
          titleControl.invalid && titleControl.touched ? 'create-vote-session-title-error' : null
        "
        class="app-input"
      />
      @if (titleControl.invalid && titleControl.touched) {
        <p id="create-vote-session-title-error" class="text-sm text-red-400">
          {{ 'voting.list.titleRequired' | transloco }}
        </p>
      }
      <div class="flex flex-wrap gap-4 text-sm text-slate-300">
        <label class="flex items-center gap-2 py-1">
          <input
            type="radio"
            class="h-4 w-4 accent-purple-600"
            name="dialog-audience"
            [checked]="selectedAudience() === 'everyone'"
            (change)="selectedAudience.set('everyone')"
          />
          {{ 'voting.list.audienceEveryone' | transloco }}
        </label>
        <label class="flex items-center gap-2 py-1">
          <input
            type="radio"
            class="h-4 w-4 accent-purple-600"
            name="dialog-audience"
            [checked]="selectedAudience() === 'subs'"
            (change)="selectedAudience.set('subs')"
          />
          {{ 'voting.list.audienceSubs' | transloco }}
        </label>
        <label class="flex items-center gap-2 py-1">
          <input
            type="radio"
            class="h-4 w-4 accent-purple-600"
            name="dialog-audience"
            [checked]="selectedAudience() === 'mods'"
            (change)="selectedAudience.set('mods')"
          />
          {{ 'voting.list.audienceMods' | transloco }}
        </label>
      </div>
      <label class="flex flex-col gap-1 text-sm text-slate-300">
        {{ 'voting.list.startCountingLabel' | transloco }}
        <app-datetime-picker [(value)]="customStartedAt" [max]="maxStartedAt" />
        <span class="text-xs text-slate-500">
          {{ 'voting.create.startPrefillHint' | transloco }}
        </span>
      </label>
      @if (errorMessage(); as message) {
        <app-notice-banner variant="error">{{ message | transloco }}</app-notice-banner>
      }
      <div class="flex justify-end gap-2">
        <button
          type="button"
          appButton="outline"
          buttonSize="lg"
          (click)="dialogRef.close(undefined)"
        >
          {{ 'common.cancel' | transloco }}
        </button>
        <button
          type="button"
          appButton="primary"
          buttonSize="lg"
          [disabled]="isSubmitting()"
          (click)="create()"
        >
          {{ 'voting.create.submit' | transloco }}
        </button>
      </div>
    </div>
  `,
})
export class CreateVoteSessionDialog {
  protected readonly data = inject<CreateVoteSessionDialogData>(DIALOG_DATA);
  protected readonly dialogRef = inject<DialogRef<VoteSessionSummary | undefined>>(DialogRef);
  private readonly voteSessionService = inject(VoteSessionService);

  protected readonly titleControl = new FormControl('', {
    nonNullable: true,
    validators: [requiredTrimmed],
  });
  protected readonly selectedAudience = signal<'everyone' | 'subs' | 'mods'>('everyone');
  // Prefilled with the start of the range the creator was just filtering on (midnight local time);
  // clearing it falls back to "count from now", same as the inline form on the voting list.
  protected readonly customStartedAt = signal(`${this.data.usageFromDate}T00:00`);
  protected readonly maxStartedAt = toLocalDateTimeInputValue(new Date());
  protected readonly errorMessage = signal<string | null>(null);
  protected readonly isSubmitting = signal(false);

  protected create(): void {
    if (this.titleControl.invalid) {
      this.titleControl.markAsTouched();
      return;
    }

    const roles =
      this.selectedAudience() === 'subs'
        ? AllowedRoles.Subs
        : this.selectedAudience() === 'mods'
          ? AllowedRoles.Mods | AllowedRoles.Broadcaster
          : AllowedRoles.Everyone;
    const startedAtLocal = this.customStartedAt();
    const startedAt = startedAtLocal ? new Date(startedAtLocal).toISOString() : undefined;

    this.errorMessage.set(null);
    this.isSubmitting.set(true);
    this.voteSessionService
      .create(
        this.data.channelName,
        this.titleControl.value.trim(),
        roles,
        startedAt,
        this.data.emoteIds,
      )
      .subscribe({
        next: (session) => this.dialogRef.close(session),
        error: (error: HttpErrorResponse) => {
          this.isSubmitting.set(false);
          this.errorMessage.set(apiErrorTranslationKey(error));
        },
      });
  }
}
