import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';

import { AuthService } from '../../core/auth/auth.service';
import { AdminChannelDto, ChannelRole, MyChannelDto } from '../../core/channels/channel.model';
import { ChannelService } from '../../core/channels/channel.service';

// Case-insensitive: the backend lowercases the value before matching its own (case-sensitive)
// pattern, but Twitch channel names are commonly typed/displayed with capitals (e.g. "HandOfBlood").
const CHANNEL_NAME_PATTERN = /^[a-zA-Z0-9_]{4,25}$/;

@Component({
  selector: 'app-overview-page',
  imports: [ReactiveFormsModule],
  template: `
    <div class="flex flex-col gap-8">
      @if (myChannels(); as channels) {
        <section>
          <h2 class="mb-3 text-lg font-medium">Meine Channels</h2>

          @if (helixUnavailable()) {
            <p class="mb-3 text-sm text-amber-400">
              Live-Moderator-Liste konnte nicht geladen werden (Twitch-Zugriffstoken abgelaufen?) —
              nur dein eigener Channel wird sicher angezeigt. Neu einloggen, um die volle Liste zu sehen.
            </p>
          }

          @if (channels.length === 0) {
            <p class="text-sm text-slate-400">Keine Channels gefunden.</p>
          } @else {
            <ul class="flex flex-col gap-2">
              @for (channel of channels; track channel.channelName) {
                <li class="flex items-center justify-between rounded-md bg-slate-900 px-4 py-3">
                  <div>
                    <span class="font-medium">#{{ channel.channelName }}</span>
                    <span class="ml-2 text-xs text-slate-500">{{ roleLabel(channel.role) }}</span>
                  </div>
                  <div class="flex items-center gap-3">
                    @if (channel.isTracked) {
                      <span [class]="channel.isBotActive ? 'text-sm text-emerald-400' : 'text-sm text-slate-500'">
                        {{ channel.isBotActive ? 'Bot aktiv' : 'Bot inaktiv' }}
                      </span>
                      <button
                        type="button"
                        class="rounded-md bg-slate-700 px-3 py-1.5 text-sm transition hover:bg-slate-600"
                        (click)="openChannel(channel.channelName)"
                      >
                        Öffnen
                      </button>
                    } @else {
                      <button
                        type="button"
                        class="rounded-md bg-purple-600 px-3 py-1.5 text-sm font-medium text-white transition hover:bg-purple-500"
                        (click)="join(channel.channelName)"
                      >
                        Hinzufügen
                      </button>
                    }
                  </div>
                </li>
              }
            </ul>
          }
        </section>
      }

      @if (adminChannels(); as channels) {
        <section>
          <h2 class="mb-3 text-lg font-medium">Alle Channels (Admin)</h2>

          <form class="mb-4 flex gap-2" (submit)="onAddChannelSubmit($event)">
            <input
              type="text"
              [formControl]="channelNameControl"
              placeholder="twitch-channel-name"
              class="flex-1 rounded-md border border-slate-700 bg-slate-950 px-3 py-2 text-sm placeholder:text-slate-600 focus:border-purple-500 focus:outline-none"
            />
            <button
              type="submit"
              class="rounded-md bg-purple-600 px-4 py-2 text-sm font-medium text-white transition hover:bg-purple-500"
            >
              Channel hinzufügen
            </button>
          </form>
          @if (channelNameControl.invalid && channelNameControl.touched) {
            <p class="mb-4 text-sm text-red-400">
              Ungültiger Channel-Name (4–25 Zeichen, Buchstaben/Ziffern/Unterstrich).
            </p>
          }

          @if (channels.length === 0) {
            <p class="text-sm text-slate-400">Keine Channels getrackt.</p>
          } @else {
            <ul class="flex flex-col gap-2">
              @for (channel of channels; track channel.channelId) {
                <li class="flex items-center justify-between rounded-md bg-slate-900 px-4 py-3">
                  <span class="font-medium">#{{ channel.channelName }}</span>
                  <div class="flex items-center gap-3">
                    <span [class]="channel.isBotActive ? 'text-sm text-emerald-400' : 'text-sm text-slate-500'">
                      {{ channel.isBotActive ? 'Bot aktiv' : 'Bot inaktiv' }}
                    </span>
                    <button
                      type="button"
                      class="rounded-md bg-slate-700 px-3 py-1.5 text-sm transition hover:bg-slate-600"
                      (click)="openChannel(channel.channelName)"
                    >
                      Öffnen
                    </button>
                  </div>
                </li>
              }
            </ul>
          }
        </section>
      }

      @if (errorMessage(); as message) {
        <p class="rounded-md bg-red-950 px-4 py-3 text-sm text-red-300">{{ message }}</p>
      }
    </div>
  `,
})
export class OverviewPage {
  private readonly channelService = inject(ChannelService);
  private readonly authService = inject(AuthService);
  private readonly router = inject(Router);

  protected readonly myChannels = signal<MyChannelDto[] | null>(null);
  protected readonly helixUnavailable = signal(false);
  protected readonly adminChannels = signal<AdminChannelDto[] | null>(null);
  protected readonly errorMessage = signal<string | null>(null);
  protected readonly channelNameControl = new FormControl('', {
    nonNullable: true,
    validators: [Validators.required, Validators.pattern(CHANNEL_NAME_PATTERN)],
  });

  constructor() {
    this.channelService.listMine().subscribe({
      next: (result) => {
        this.myChannels.set(result.channels);
        this.helixUnavailable.set(result.helixUnavailable);
      },
      error: (error: HttpErrorResponse) => this.handleError(error),
    });

    this.channelService.listAll().subscribe({
      next: (channels) => this.adminChannels.set(channels),
      // 403 = not a global admin — the expected case for most users, not an error to surface.
      error: () => this.adminChannels.set(null),
    });
  }

  protected roleLabel(role: ChannelRole): string {
    return role === ChannelRole.Broadcaster ? 'Broadcaster' : 'Moderator';
  }

  protected join(channelName: string): void {
    this.channelService.join(channelName).subscribe({
      next: () => this.openChannel(channelName),
      error: (error: HttpErrorResponse) => this.handleError(error),
    });
  }

  protected onAddChannelSubmit(event: Event): void {
    event.preventDefault();

    if (this.channelNameControl.invalid) {
      this.channelNameControl.markAsTouched();
      return;
    }

    const channelName = this.channelNameControl.value.trim().toLowerCase();
    this.channelNameControl.reset('');
    this.join(channelName);
  }

  protected openChannel(channelName: string): void {
    this.router.navigate(['/channels', channelName]);
  }

  private handleError(error: HttpErrorResponse): void {
    if (error.status === 401) {
      this.authService.handleSessionExpired();
      return;
    }
    this.errorMessage.set('Etwas ist schiefgelaufen. Bitte versuch es erneut.');
  }
}
