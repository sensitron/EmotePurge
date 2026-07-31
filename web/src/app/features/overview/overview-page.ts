import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';

import { AuthService } from '../../core/auth/auth.service';
import { MyChannelDto } from '../../core/channels/channel.model';
import { ChannelService } from '../../core/channels/channel.service';
import { apiErrorTranslationKey } from '../../core/i18n/api-error';
import { WorkerHealthService } from '../../core/health/worker-health.service';
import { Button } from '../../shared/ui/button';
import { EmptyState } from '../../shared/ui/empty-state';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { SkeletonRows } from '../../shared/ui/skeleton-rows';
import { StatusBadge } from '../../shared/ui/status-badge';

@Component({
  selector: 'app-overview-page',
  imports: [Button, EmptyState, NoticeBanner, RouterLink, SkeletonRows, StatusBadge, TranslocoPipe],
  templateUrl: './overview-page.html',
})
export class OverviewPage {
  private readonly authService = inject(AuthService);
  private readonly channelService = inject(ChannelService);
  private readonly router = inject(Router);
  private readonly workerHealthService = inject(WorkerHealthService);

  // The header dot alone is a 10px signal nobody notices — while the worker is down, nothing is
  // being counted, which deserves a real page-level notice on the entry page.
  protected readonly workerDisconnected = computed(
    () => this.workerHealthService.status() === 'stale',
  );

  protected readonly myChannels = signal<MyChannelDto[] | null>(null);
  protected readonly helixUnavailable = signal(false);
  protected readonly reauthRequired = signal(false);
  protected readonly sevenTvUnavailable = signal(false);
  protected readonly errorMessage = signal<string | null>(null);

  constructor() {
    this.channelService.listMine().subscribe({
      next: (result) => {
        this.myChannels.set(result.channels);
        this.helixUnavailable.set(result.helixUnavailable);
        this.reauthRequired.set(result.reauthRequired);
        this.sevenTvUnavailable.set(result.sevenTvUnavailable);
      },
      error: (error: HttpErrorResponse) => this.handleError(error),
    });
  }

  protected join(channelName: string): void {
    this.channelService.join(channelName).subscribe({
      next: () => this.openChannel(channelName),
      error: (error: HttpErrorResponse) => this.handleError(error),
    });
  }

  // Same call as join(), but stays on the overview and flips the row in place — someone is likely
  // reactivating one of several channels, and being navigated away after each one is in the way.
  protected reactivate(channelName: string): void {
    this.channelService.join(channelName).subscribe({
      next: () => {
        this.myChannels.update(
          (channels) =>
            channels?.map((c) =>
              c.channelName === channelName ? { ...c, isTracked: true, isBotActive: true } : c,
            ) ?? null,
        );
      },
      error: (error: HttpErrorResponse) => this.handleError(error),
    });
  }

  protected openChannel(channelName: string): void {
    this.router.navigate(['/channels', channelName]);
  }

  // Fresh Twitch OAuth round-trip (full browser redirect). Returning to the overview afterwards
  // is exactly the backend's default post-login redirect, so no returnUrl stash is needed.
  protected relogin(): void {
    this.authService.login();
  }

  // 401 is not handled here — apiAuthInterceptor resets the session and redirects for every
  // /api/ call in the app.
  private handleError(error: HttpErrorResponse): void {
    this.errorMessage.set(apiErrorTranslationKey(error));
  }
}
