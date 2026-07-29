import { HttpErrorResponse } from '@angular/common/http';
import { Component, effect, inject, input, signal } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';

import { ChannelService } from '../../core/channels/channel.service';
import { UsageStatService } from '../../core/usage-stats/usage-stat.service';

@Component({
  selector: 'app-channel-workspace-layout',
  imports: [RouterLink, RouterLinkActive, RouterOutlet, TranslocoPipe],
  template: `
    <div>
      <div class="mb-4 flex items-center justify-between">
        <div class="flex items-center gap-4">
          <a
            routerLink="/"
            class="rounded-md border border-purple-700 px-3 py-1.5 text-sm text-purple-400 transition hover:bg-purple-950"
          >
            ← {{ 'channelWorkspace.backToOverview' | transloco }}
          </a>
          <h1 class="text-xl font-semibold">#{{ channelName() }}</h1>
        </div>
        @if (canManage()) {
          @if (isBotActive()) {
            <button
              type="button"
              class="rounded-md border border-red-800 px-3 py-1.5 text-sm text-red-400 transition hover:bg-red-950"
              (click)="leave()"
            >
              {{ 'channelWorkspace.leaveChannel' | transloco }}
            </button>
          } @else {
            <button
              type="button"
              class="rounded-md bg-purple-600 px-3 py-1.5 text-sm font-medium text-white transition hover:bg-purple-500 disabled:opacity-50"
              [disabled]="rejoinInProgress()"
              (click)="rejoin()"
            >
              {{ 'channelWorkspace.rejoinChannel' | transloco }}
            </button>
          }
        }
      </div>

      <!-- An inactive bot collects nothing, but every page below still renders its historical data
           as usual — without this the channel looks healthy while silently recording nothing. -->
      @if (canManage() && !isBotActive()) {
        <p class="mb-4 rounded-md bg-amber-950/40 px-4 py-3 text-sm text-amber-300" role="status">
          {{ 'channelWorkspace.botInactiveNotice' | transloco }}
        </p>
      }

      @if (errorMessage(); as message) {
        <p class="mb-4 rounded-md bg-red-950 px-4 py-3 text-sm text-red-300" role="alert">{{ message | transloco }}</p>
      }

      <nav class="mb-6 flex gap-2 border-b border-slate-800">
        @if (canViewUsageStats()) {
          <a
            routerLink="usage-stats"
            routerLinkActive
            #usageStatsTab="routerLinkActive"
            [class]="
              usageStatsTab.isActive
                ? 'border-b-2 border-purple-500 px-3 py-2 text-sm text-slate-100 transition'
                : 'border-b-2 border-transparent px-3 py-2 text-sm text-slate-400 transition hover:text-slate-200'
            "
          >
            {{ 'channelWorkspace.tabs.usage' | transloco }}
          </a>
        }
        <a
          routerLink="vote-sessions"
          routerLinkActive
          #voteSessionsTab="routerLinkActive"
          [class]="
            voteSessionsTab.isActive
              ? 'border-b-2 border-purple-500 px-3 py-2 text-sm text-slate-100 transition'
              : 'border-b-2 border-transparent px-3 py-2 text-sm text-slate-400 transition hover:text-slate-200'
          "
        >
          {{ 'channelWorkspace.tabs.voting' | transloco }}
        </a>
      </nav>

      <router-outlet />
    </div>
  `,
})
export class ChannelWorkspaceLayout {
  readonly channelName = input.required<string>();

  private readonly channelService = inject(ChannelService);
  private readonly usageStatService = inject(UsageStatService);
  private readonly router = inject(Router);
  private readonly translocoService = inject(TranslocoService);

  // ChannelManagementAuthorizationFilter probe — hides "Channel verlassen" for anyone who isn't
  // actually allowed to manage this channel (anonymous visitors and unrelated logged-in users
  // alike), not just unauthenticated ones. This is the UI-visibility half, not the enforcement —
  // a direct action still goes through the server-side filter regardless.
  protected readonly canManage = signal(false);

  // Weaker, separate probe for the "Nutzung" tab: UsageStatsAccessAuthorizationFilter additionally
  // admits a channel's 7TV editors, who aren't allowed to manage the channel (join/leave, vote
  // sessions) at all — so this can't just reuse `canManage`. The route itself is additionally
  // guarded (usageStatsAccessGuard) — this is only the UI-visibility half.
  protected readonly canViewUsageStats = signal(false);

  // Comes free with the canManage probe, which already fetches the channel status and used to
  // discard everything but the success/failure. Without it a deactivated channel offered no way back
  // in: leaving keeps the row (see ChannelService.LeaveAsync), so the overview lists it as tracked
  // and never shows the "Hinzufügen" button again — a non-admin was stuck with a permanently silent
  // bot and no control anywhere in the UI.
  protected readonly isBotActive = signal(true);
  protected readonly rejoinInProgress = signal(false);

  protected readonly errorMessage = signal<string | null>(null);

  constructor() {
    effect(() => this.probeCanManage());
    effect(() => this.probeCanViewUsageStats());
  }

  private probeCanManage(): void {
    this.channelService.getStatus(this.channelName()).subscribe({
      next: (status) => {
        this.canManage.set(true);
        this.isBotActive.set(status.isBotActive);
      },
      error: () => this.canManage.set(false),
    });
  }

  private probeCanViewUsageStats(): void {
    const today = new Date().toISOString().slice(0, 10);
    this.usageStatService.getTotals(this.channelName(), today, today).subscribe({
      next: () => this.canViewUsageStats.set(true),
      error: () => this.canViewUsageStats.set(false),
    });
  }

  protected leave(): void {
    // A leave now only deactivates the bot and keeps all history (see ChannelService.LeaveAsync) —
    // reversible by rejoining. Still confirmed, because it stops data collection for the channel.
    const confirmed = window.confirm(
      this.translocoService.translate('channelWorkspace.leaveConfirm', { channelName: this.channelName() }),
    );
    if (!confirmed) {
      return;
    }

    this.channelService.leave(this.channelName()).subscribe({
      next: () => this.router.navigateByUrl('/'),
      error: (error: HttpErrorResponse) => {
        this.errorMessage.set(
          error.status === 403 ? 'channelWorkspace.errors.leaveForbidden' : 'channelWorkspace.errors.leaveFailed',
        );
      },
    });
  }

  // Deliberately no confirmation and no navigation: reactivating is non-destructive and the admin is
  // already on the page they want to keep working on.
  protected rejoin(): void {
    this.rejoinInProgress.set(true);
    this.errorMessage.set(null);

    this.channelService.join(this.channelName()).subscribe({
      next: () => {
        this.isBotActive.set(true);
        this.rejoinInProgress.set(false);
      },
      error: (error: HttpErrorResponse) => {
        this.rejoinInProgress.set(false);
        this.errorMessage.set(
          error.status === 403 ? 'channelWorkspace.errors.leaveForbidden' : 'channelWorkspace.errors.rejoinFailed',
        );
      },
    });
  }
}
