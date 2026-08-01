import { Dialog } from '@angular/cdk/dialog';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, effect, inject, input, signal } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';

import { ChannelService } from '../../core/channels/channel.service';
import { SevenTvDeleteService } from '../../core/seven-tv/seven-tv-delete.service';
import { BackLink } from '../../shared/ui/back-link';
import { Button } from '../../shared/ui/button';
import { ConfirmDialog, ConfirmDialogData } from '../../shared/ui/confirm-dialog';
import { NoticeBanner } from '../../shared/ui/notice-banner';

@Component({
  selector: 'app-channel-workspace-layout',
  imports: [
    BackLink,
    Button,
    NoticeBanner,
    RouterLink,
    RouterLinkActive,
    RouterOutlet,
    TranslocoPipe,
  ],
  template: `
    <div>
      <div class="mb-4 flex flex-wrap items-center gap-x-4 gap-y-2">
        <app-back-link link="/" [label]="'nav.overview' | transloco" />
        <!-- On narrow viewports the title takes its own full-width line below the buttons instead
             of being squeezed to an ellipsis between them; from md: it sits inline as before. -->
        <h1
          class="order-last w-full truncate text-2xl font-bold tracking-tight md:order-0 md:w-auto md:min-w-0 md:flex-1"
        >
          #{{ channelName() }}
        </h1>
        @if (canManage()) {
          @if (isBotActive()) {
            <button type="button" appButton="danger" class="ml-auto" (click)="leave()">
              {{ 'channelWorkspace.leaveChannel' | transloco }}
            </button>
          } @else {
            <button
              type="button"
              appButton="primary"
              class="ml-auto"
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
        <app-notice-banner variant="warning" class="mb-4 block">
          {{ 'channelWorkspace.botInactiveNotice' | transloco }}
        </app-notice-banner>
      }

      @if (errorMessage(); as message) {
        <app-notice-banner variant="error" class="mb-4 block">{{
          message | transloco
        }}</app-notice-banner>
      }

      <!-- Sticky under the h-14 shell header; h-10 is a contract — filter toolbars pin at
           top-24 (= 14 + 10). Links are flex/items-center so the fixed height carries exactly. -->
      <nav class="app-sticky-bar top-14 mb-6 flex h-10 gap-2 border-b border-slate-800">
        @if (canViewUsageStats()) {
          <a
            routerLink="usage-stats"
            routerLinkActive
            ariaCurrentWhenActive="page"
            #usageStatsTab="routerLinkActive"
            [class]="
              usageStatsTab.isActive
                ? 'flex items-center border-b-2 border-purple-500 px-3 text-sm text-slate-100 transition'
                : 'flex items-center border-b-2 border-transparent px-3 text-sm text-slate-400 transition hover:text-slate-200'
            "
          >
            {{ 'channelWorkspace.tabs.usage' | transloco }}
          </a>
        }
        <a
          routerLink="vote-sessions"
          routerLinkActive
          ariaCurrentWhenActive="page"
          #voteSessionsTab="routerLinkActive"
          [class]="
            voteSessionsTab.isActive
              ? 'flex items-center border-b-2 border-purple-500 px-3 text-sm text-slate-100 transition'
              : 'flex items-center border-b-2 border-transparent px-3 text-sm text-slate-400 transition hover:text-slate-200'
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
  private readonly deleteService = inject(SevenTvDeleteService);
  private readonly router = inject(Router);
  private readonly translocoService = inject(TranslocoService);
  private readonly dialog = inject(Dialog);

  // Was two probes: GET /api/channels/{c} for "may manage" and a throwaway one-day
  // GetUsageTotalsAsync call for "may see the usage tab" (weaker — it also admits the channel's 7TV
  // editors, who may not manage the channel at all, so it cannot just reuse canManage). Both are now
  // fields of one /permissions response. This is the UI-visibility half only: every action still goes
  // through the server-side filter, and the usage route has its own guard.
  protected readonly canManage = signal(false);
  protected readonly canViewUsageStats = signal(false);

  // Without this a deactivated channel offered no way back in: leaving keeps the row (see
  // ChannelService.LeaveAsync), so the overview lists it as tracked and never shows the "Hinzufügen"
  // button again — a non-admin was stuck with a permanently silent bot and no control anywhere in the
  // UI. Starts true so the leave button does not flicker into a reactivate button on load.
  protected readonly isBotActive = signal(true);
  protected readonly rejoinInProgress = signal(false);

  protected readonly errorMessage = signal<string | null>(null);

  constructor() {
    effect(() => {
      const channelName = this.channelName();
      // A finished mass-delete run from another channel must not follow the user in here.
      this.deleteService.resetIfChannelChanged(channelName);
      this.loadPermissions(channelName);
    });
  }

  protected leave(): void {
    // A leave now only deactivates the bot and keeps all history (see ChannelService.LeaveAsync) —
    // reversible by rejoining. Still confirmed, because it stops data collection for the channel.
    const data: ConfirmDialogData = {
      message: this.translocoService.translate('channelWorkspace.leaveConfirm', {
        channelName: this.channelName(),
      }),
      confirmLabel: this.translocoService.translate('channelWorkspace.leaveChannel'),
    };
    this.dialog
      .open<boolean>(ConfirmDialog, {
        data,
        backdropClass: 'app-dialog-backdrop',
        panelClass: 'app-dialog-panel',
      })
      .closed.subscribe((confirmed) => {
        if (!confirmed) {
          return;
        }
        this.channelService.leave(this.channelName()).subscribe({
          next: () => this.router.navigateByUrl('/'),
          error: (error: HttpErrorResponse) => {
            this.errorMessage.set(
              error.status === 403
                ? 'channelWorkspace.errors.leaveForbidden'
                : 'channelWorkspace.errors.leaveFailed',
            );
          },
        });
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
          error.status === 403
            ? 'channelWorkspace.errors.leaveForbidden'
            : 'channelWorkspace.errors.rejoinFailed',
        );
      },
    });
  }

  private loadPermissions(channelName: string): void {
    this.channelService.getPermissions(channelName).subscribe({
      next: (permissions) => {
        this.canManage.set(permissions.canManage);
        this.canViewUsageStats.set(permissions.canViewUsageStats);
        this.isBotActive.set(permissions.isBotActive);
      },
      // Only reachable for a logged-out user (the interceptor already redirects) or a server error —
      // hide everything privileged rather than guess.
      error: () => {
        this.canManage.set(false);
        this.canViewUsageStats.set(false);
      },
    });
  }
}
