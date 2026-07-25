import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';

import { AuthService } from '../../core/auth/auth.service';
import { ChannelStatus } from '../../core/channels/channel.model';
import { ChannelService } from '../../core/channels/channel.service';

// Case-insensitive: the backend lowercases the value before matching its own (case-sensitive)
// pattern, but Twitch channel names are commonly typed/displayed with capitals (e.g. "HandOfBlood").
const CHANNEL_NAME_PATTERN = /^[a-zA-Z0-9_]{4,25}$/;

type ViewState =
  | { kind: 'idle' }
  | { kind: 'loading' }
  | { kind: 'not-tracked' }
  | { kind: 'active'; status: ChannelStatus }
  | { kind: 'error'; message: string };

@Component({
  selector: 'app-dashboard-page',
  imports: [ReactiveFormsModule],
  template: `
    <div class="min-h-screen bg-slate-950 px-4 py-10 text-slate-100">
      <div class="mx-auto max-w-xl">
        <header class="mb-8 flex items-center justify-between">
          <div>
            <h1 class="text-xl font-semibold">Emote Purge</h1>
            @if (currentUser(); as user) {
              <p class="text-sm text-slate-400">Eingeloggt als {{ user.displayName }}</p>
            }
          </div>
          <button
            type="button"
            class="rounded-md border border-slate-700 px-3 py-1.5 text-sm text-slate-300 transition hover:bg-slate-800"
            (click)="logout()"
          >
            Logout
          </button>
        </header>

        <section class="rounded-lg bg-slate-900 p-6 shadow-xl">
          <h2 class="mb-4 text-lg font-medium">Bot verwalten</h2>

          <form class="flex gap-2" (submit)="onSubmit($event)">
            <input
              type="text"
              [formControl]="channelNameControl"
              placeholder="twitch-channel-name"
              class="flex-1 rounded-md border border-slate-700 bg-slate-950 px-3 py-2 text-sm placeholder:text-slate-600 focus:border-purple-500 focus:outline-none"
            />
            <button
              type="submit"
              class="rounded-md bg-slate-700 px-4 py-2 text-sm font-medium transition hover:bg-slate-600"
            >
              Status prüfen
            </button>
          </form>

          @if (channelNameControl.invalid && channelNameControl.touched) {
            <p class="mt-2 text-sm text-red-400">
              Ungültiger Channel-Name (4–25 Zeichen, Kleinbuchstaben/Ziffern/Unterstrich).
            </p>
          }

          <div class="mt-6">
            @if (viewState(); as state) {
              @switch (state.kind) {
                @case ('loading') {
                  <p class="text-sm text-slate-400">Lädt…</p>
                }
                @case ('not-tracked') {
                  <div class="flex items-center justify-between rounded-md bg-slate-800 px-4 py-3">
                    <span class="text-sm text-slate-300">Channel wird aktuell nicht getrackt.</span>
                    <button
                      type="button"
                      class="rounded-md bg-purple-600 px-3 py-1.5 text-sm font-medium transition hover:bg-purple-500"
                      (click)="join()"
                    >
                      Join
                    </button>
                  </div>
                }
                @case ('active') {
                  <div class="flex items-center justify-between rounded-md bg-slate-800 px-4 py-3">
                    <span class="text-sm text-emerald-400">Bot ist aktiv in #{{ state.status.channelName }}.</span>
                    <button
                      type="button"
                      class="rounded-md bg-red-600 px-3 py-1.5 text-sm font-medium transition hover:bg-red-500"
                      (click)="leave()"
                    >
                      Leave
                    </button>
                  </div>
                }
                @case ('error') {
                  <p class="rounded-md bg-red-950 px-4 py-3 text-sm text-red-300">
                    {{ state.message }}
                  </p>
                }
              }
            }
          </div>
        </section>
      </div>
    </div>
  `,
})
export class DashboardPage {
  private readonly authService = inject(AuthService);
  private readonly channelService = inject(ChannelService);

  protected readonly currentUser = this.authService.currentUser;
  protected readonly channelNameControl = new FormControl('', {
    nonNullable: true,
    validators: [Validators.required, Validators.pattern(CHANNEL_NAME_PATTERN)],
  });
  protected readonly viewState = signal<ViewState>({ kind: 'idle' });

  protected logout(): void {
    this.authService.logout();
  }

  protected onSubmit(event: Event): void {
    event.preventDefault();
    this.checkStatus();
  }

  protected checkStatus(): void {
    if (this.channelNameControl.invalid) {
      this.channelNameControl.markAsTouched();
      return;
    }

    const channelName = this.normalizedChannelName();
    this.viewState.set({ kind: 'loading' });

    this.channelService.getStatus(channelName).subscribe({
      next: (status) => this.viewState.set({ kind: 'active', status }),
      error: (error: HttpErrorResponse) => this.handleError(error),
    });
  }

  protected join(): void {
    const channelName = this.normalizedChannelName();
    this.viewState.set({ kind: 'loading' });

    this.channelService.join(channelName).subscribe({
      next: (status) => this.viewState.set({ kind: 'active', status }),
      error: (error: HttpErrorResponse) => this.handleError(error),
    });
  }

  protected leave(): void {
    const channelName = this.normalizedChannelName();
    this.viewState.set({ kind: 'loading' });

    this.channelService.leave(channelName).subscribe({
      next: () => this.viewState.set({ kind: 'not-tracked' }),
      error: (error: HttpErrorResponse) => this.handleError(error),
    });
  }

  private normalizedChannelName(): string {
    return this.channelNameControl.value.trim().toLowerCase();
  }

  private handleError(error: HttpErrorResponse): void {
    if (error.status === 404) {
      this.viewState.set({ kind: 'not-tracked' });
      return;
    }

    if (error.status === 401) {
      this.authService.handleSessionExpired();
      return;
    }

    if (error.status === 403) {
      this.viewState.set({
        kind: 'error',
        message:
          'Nicht berechtigt, diesen Channel zu verwalten. Falls du Mod/Broadcaster bist, versuch dich neu einzuloggen — dein Twitch-Zugriffstoken könnte abgelaufen sein.',
      });
      return;
    }

    if (error.status === 400) {
      this.viewState.set({ kind: 'error', message: 'Ungültiger Channel-Name.' });
      return;
    }

    this.viewState.set({ kind: 'error', message: 'Etwas ist schiefgelaufen. Bitte versuch es erneut.' });
  }
}
