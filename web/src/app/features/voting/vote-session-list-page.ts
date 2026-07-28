import { HttpErrorResponse } from '@angular/common/http';
import { Component, effect, inject, input, signal } from '@angular/core';
import { AbstractControl, FormControl, ReactiveFormsModule, ValidationErrors } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';

import { AuthService } from '../../core/auth/auth.service';
import { ChannelService } from '../../core/channels/channel.service';
import { apiErrorTranslationKey } from '../../core/i18n/api-error';
import { AllowedRoles, VoteSessionSummary } from '../../core/voting/vote-session.model';
import { VoteSessionService } from '../../core/voting/vote-session.service';
import { DateTimePicker } from '../../shared/datetime/datetime-picker';
import { Pager } from '../../shared/pagination/pager';

function requiredTrimmed(control: AbstractControl<string>): ValidationErrors | null {
  return control.value?.trim().length > 0 ? null : { required: true };
}

function toLocalDateTimeInputValue(date: Date): string {
  const pad = (n: number) => n.toString().padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

@Component({
  selector: 'app-vote-session-list-page',
  imports: [ReactiveFormsModule, RouterLink, DateTimePicker, Pager, TranslocoPipe],
  templateUrl: './vote-session-list-page.html',
})
export class VoteSessionListPage {
  readonly channelName = input.required<string>();

  private readonly voteSessionService = inject(VoteSessionService);
  private readonly channelService = inject(ChannelService);
  private readonly authService = inject(AuthService);
  private readonly translocoService = inject(TranslocoService);

  protected readonly sessions = signal<VoteSessionSummary[]>([]);
  protected readonly page = signal(1);
  protected readonly totalPages = signal(0);
  // Reuses the ChannelManagementAuthorizationFilter semantics as a de-facto permission probe
  // (200 = can manage, 403 = plain voter/anonymous) instead of adding a new public "canManage" field.
  protected readonly canManage = signal(false);
  protected readonly errorMessage = signal<string | null>(null);

  protected readonly titleControl = new FormControl('', { nonNullable: true, validators: [requiredTrimmed] });
  protected readonly selectedAudience = signal<'everyone' | 'subs' | 'mods'>('everyone');
  protected readonly customStartedAt = signal('');
  protected readonly maxStartedAt = toLocalDateTimeInputValue(new Date());

  protected readonly copyFeedback = signal<{ sessionId: number; status: 'copied' | 'error' } | null>(null);
  private copyFeedbackTimeout?: ReturnType<typeof setTimeout>;

  constructor() {
    // Deferred, not called directly — `channelName()` is a required route-bound input and isn't
    // set yet while the constructor body runs; reading it here throws NG0950. effect() defers to
    // after Angular has applied inputs, same fix already used in UsageStatsPage.
    effect(() => this.load());
  }

  private load(): void {
    const channelName = this.channelName();

    this.voteSessionService.list(channelName, this.page()).subscribe({
      next: (result) => {
        this.sessions.set(result.items);
        this.totalPages.set(result.totalPages);
      },
      error: (error: HttpErrorResponse) => this.handleError(error),
    });

    this.channelService.getStatus(channelName).subscribe({
      next: () => this.canManage.set(true),
      error: () => this.canManage.set(false),
    });
  }

  protected onPageChange(newPage: number): void {
    this.page.set(newPage);
    this.load();
  }

  protected createSession(): void {
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

    this.voteSessionService.create(this.channelName(), this.titleControl.value.trim(), roles, startedAt).subscribe({
      next: (session) => {
        this.sessions.update((sessions) => [session, ...sessions]);
        this.titleControl.reset('');
        this.customStartedAt.set('');
      },
      error: (error: HttpErrorResponse) => this.handleError(error),
    });
  }

  protected endSession(sessionId: number): void {
    this.voteSessionService.end(this.channelName(), sessionId).subscribe({
      next: (updated) => {
        this.sessions.update((sessions) => sessions.map((session) => (session.id === updated.id ? updated : session)));
      },
      error: (error: HttpErrorResponse) => this.handleError(error),
    });
  }

  protected deleteSession(sessionId: number): void {
    if (!window.confirm(this.translocoService.translate('voting.list.deleteConfirm'))) {
      return;
    }

    this.voteSessionService.delete(this.channelName(), sessionId).subscribe({
      next: () => this.sessions.update((sessions) => sessions.filter((session) => session.id !== sessionId)),
      error: (error: HttpErrorResponse) => this.handleError(error),
    });
  }

  protected copyShareLink(sessionId: number): void {
    const url = `${window.location.origin}/channels/${this.channelName()}/vote-sessions/${sessionId}`;
    const setFeedback = (status: 'copied' | 'error') => {
      clearTimeout(this.copyFeedbackTimeout);
      this.copyFeedback.set({ sessionId, status });
      this.copyFeedbackTimeout = setTimeout(() => this.copyFeedback.set(null), 2000);
    };

    if (!navigator.clipboard) {
      setFeedback('error');
      return;
    }
    navigator.clipboard.writeText(url).then(
      () => setFeedback('copied'),
      () => setFeedback('error'),
    );
  }

  private handleError(error: HttpErrorResponse): void {
    if (error.status === 401) {
      this.authService.handleSessionExpired();
      return;
    }
    this.errorMessage.set(apiErrorTranslationKey(error));
  }
}
