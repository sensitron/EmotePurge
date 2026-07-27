import { HttpErrorResponse } from '@angular/common/http';
import { Component, effect, inject, input, signal } from '@angular/core';
import { AbstractControl, FormControl, ReactiveFormsModule, ValidationErrors } from '@angular/forms';
import { RouterLink } from '@angular/router';

import { AuthService } from '../../core/auth/auth.service';
import { ChannelService } from '../../core/channels/channel.service';
import { AllowedRoles, VoteSessionSummary } from '../../core/voting/vote-session.model';
import { VoteSessionService } from '../../core/voting/vote-session.service';
import { DateTimePicker } from '../../shared/datetime/datetime-picker';

function requiredTrimmed(control: AbstractControl<string>): ValidationErrors | null {
  return control.value?.trim().length > 0 ? null : { required: true };
}

function toLocalDateTimeInputValue(date: Date): string {
  const pad = (n: number) => n.toString().padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

@Component({
  selector: 'app-vote-session-list-page',
  imports: [ReactiveFormsModule, RouterLink, DateTimePicker],
  template: `
    <div class="flex flex-col gap-6">
      @if (canManage()) {
        <section class="rounded-lg bg-slate-900 p-5">
          <h2 class="mb-3 text-lg font-medium">Neue Abstimmung erstellen</h2>
          <div class="flex flex-col gap-3">
            <input
              type="text"
              [formControl]="titleControl"
              placeholder="Titel, z.B. Monats-Aufräumaktion Juli"
              class="rounded-md border border-slate-700 bg-slate-950 px-3 py-2 text-sm placeholder:text-slate-600 focus:border-purple-500 focus:outline-none"
            />
            @if (titleControl.invalid && titleControl.touched) {
              <p class="text-xs text-red-400">Titel darf nicht leer sein.</p>
            }
            <div class="flex gap-4 text-sm text-slate-300">
              <label class="flex items-center gap-2">
                <input
                  type="radio"
                  name="audience"
                  [checked]="selectedAudience() === 'everyone'"
                  (change)="selectedAudience.set('everyone')"
                />
                Alle
              </label>
              <label class="flex items-center gap-2">
                <input
                  type="radio"
                  name="audience"
                  [checked]="selectedAudience() === 'subs'"
                  (change)="selectedAudience.set('subs')"
                />
                Nur Subs
              </label>
              <label class="flex items-center gap-2">
                <input
                  type="radio"
                  name="audience"
                  [checked]="selectedAudience() === 'mods'"
                  (change)="selectedAudience.set('mods')"
                />
                Nur Mods/Streamer
              </label>
            </div>
            <label class="flex flex-col gap-1 text-sm text-slate-300">
              Zählung ab (optional, Standard: jetzt)
              <app-datetime-picker [(value)]="customStartedAt" [max]="maxStartedAt" placeholder="Jetzt (Standard)" />
            </label>
            <button
              type="button"
              class="self-start rounded-md bg-purple-600 px-4 py-2 text-sm font-medium text-white transition hover:bg-purple-500"
              (click)="createSession()"
            >
              Erstellen
            </button>
          </div>
        </section>
      }

      @if (errorMessage(); as message) {
        <p class="rounded-md bg-red-950 px-4 py-3 text-sm text-red-300">{{ message }}</p>
      }

      @if (sessions().length === 0) {
        <p class="text-sm text-slate-400">Noch keine Abstimmungen für diesen Channel.</p>
      } @else {
        <ul class="flex flex-col gap-2">
          @for (session of sessions(); track session.id) {
            <li class="rounded-md bg-slate-900 px-4 py-3">
              <div class="flex items-center justify-between">
                <a [routerLink]="['/channels', channelName(), 'vote-sessions', session.id]" class="font-medium hover:underline">
                  {{ session.title }}
                </a>
                <span [class]="session.isActive ? 'text-sm text-emerald-400' : 'text-sm text-slate-500'">
                  {{ session.isActive ? 'Aktiv' : 'Beendet' }}
                </span>
              </div>
              <div class="mt-2 flex items-center gap-3 text-sm">
                <button type="button" class="text-slate-400 hover:underline" (click)="copyShareLink(session.id)">
                  Link kopieren
                </button>
                @if (copyFeedback(); as feedback) {
                  @if (feedback.sessionId === session.id) {
                    <span [class]="feedback.status === 'copied' ? 'text-xs text-emerald-400' : 'text-xs text-red-400'">
                      {{ feedback.status === 'copied' ? '✓ Kopiert' : 'Kopieren fehlgeschlagen' }}
                    </span>
                  }
                }
                @if (canManage() && session.isActive) {
                  <button type="button" class="text-red-400 hover:underline" (click)="endSession(session.id)">
                    Beenden
                  </button>
                }
              </div>
            </li>
          }
        </ul>
      }
    </div>
  `,
})
export class VoteSessionListPage {
  readonly channelName = input.required<string>();

  private readonly voteSessionService = inject(VoteSessionService);
  private readonly channelService = inject(ChannelService);
  private readonly authService = inject(AuthService);

  protected readonly sessions = signal<VoteSessionSummary[]>([]);
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

    this.voteSessionService.list(channelName).subscribe({
      next: (sessions) => this.sessions.set(sessions),
      error: (error: HttpErrorResponse) => this.handleError(error),
    });

    this.channelService.getStatus(channelName).subscribe({
      next: () => this.canManage.set(true),
      error: () => this.canManage.set(false),
    });
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
    this.errorMessage.set('Etwas ist schiefgelaufen. Bitte versuch es erneut.');
  }
}
