import { HttpErrorResponse } from '@angular/common/http';
import { Component, effect, inject, input, signal } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

import { ChannelService } from '../../core/channels/channel.service';

@Component({
  selector: 'app-channel-workspace-layout',
  imports: [RouterLink, RouterLinkActive, RouterOutlet],
  template: `
    <div>
      <div class="mb-4 flex items-center justify-between">
        <h1 class="text-xl font-semibold">#{{ channelName() }}</h1>
        <div class="flex items-center gap-4">
          <a routerLink="/" class="text-sm text-slate-400 transition hover:text-slate-200">← Übersicht</a>
          @if (canManage()) {
            <button
              type="button"
              class="rounded-md border border-red-800 px-3 py-1.5 text-sm text-red-400 transition hover:bg-red-950"
              (click)="leave()"
            >
              Channel verlassen
            </button>
          }
        </div>
      </div>

      @if (errorMessage(); as message) {
        <p class="mb-4 rounded-md bg-red-950 px-4 py-3 text-sm text-red-300">{{ message }}</p>
      }

      <nav class="mb-6 flex gap-2 border-b border-slate-800">
        @if (canManage()) {
          <a
            routerLink="usage-stats"
            routerLinkActive="border-purple-500 text-slate-100"
            class="border-b-2 border-transparent px-3 py-2 text-sm text-slate-400 transition hover:text-slate-200"
          >
            Nutzung
          </a>
        }
        <a
          routerLink="vote-sessions"
          routerLinkActive="border-purple-500 text-slate-100"
          class="border-b-2 border-transparent px-3 py-2 text-sm text-slate-400 transition hover:text-slate-200"
        >
          Votings
        </a>
      </nav>

      <router-outlet />
    </div>
  `,
})
export class ChannelWorkspaceLayout {
  readonly channelName = input.required<string>();

  private readonly channelService = inject(ChannelService);
  private readonly router = inject(Router);

  // Same ChannelManagementAuthorizationFilter probe used by the voting pages — hides both the
  // "Nutzung" tab and "Channel verlassen" for anyone who isn't actually allowed to manage this
  // channel (anonymous visitors and unrelated logged-in users alike), not just unauthenticated ones.
  // The route itself is additionally guarded (channelManagementGuard) — this is the UI-visibility
  // half, not the enforcement; a direct URL hit still goes through the guard + the server-side filter.
  protected readonly canManage = signal(false);
  protected readonly errorMessage = signal<string | null>(null);

  constructor() {
    effect(() => this.probeCanManage());
  }

  private probeCanManage(): void {
    this.channelService.getStatus(this.channelName()).subscribe({
      next: () => this.canManage.set(true),
      error: () => this.canManage.set(false),
    });
  }

  protected leave(): void {
    // A leave hard-deletes the channel row and cascades all Emote/UsageStat/VoteSession rows
    // (existing backend behavior, see CLAUDE.md decision log) — worth a confirm, not a silent click.
    const confirmed = window.confirm(
      `Channel #${this.channelName()} wirklich verlassen? Alle Emotes, Nutzungsstatistiken und Abstimmungen dieses Channels werden dabei unwiderruflich gelöscht.`,
    );
    if (!confirmed) {
      return;
    }

    this.channelService.leave(this.channelName()).subscribe({
      next: () => this.router.navigateByUrl('/'),
      error: (error: HttpErrorResponse) => {
        this.errorMessage.set(
          error.status === 403 ? 'Nicht berechtigt, diesen Channel zu verlassen.' : 'Channel konnte nicht verlassen werden.',
        );
      },
    });
  }
}
