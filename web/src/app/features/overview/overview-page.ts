import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';

import { AuthService } from '../../core/auth/auth.service';
import { AdminChannelDto, MyChannelDto } from '../../core/channels/channel.model';
import { ChannelService } from '../../core/channels/channel.service';
import { apiErrorTranslationKey } from '../../core/i18n/api-error';

// Case-insensitive: the backend lowercases the value before matching its own (case-sensitive)
// pattern, but Twitch channel names are commonly typed/displayed with capitals (e.g. "HandOfBlood").
const CHANNEL_NAME_PATTERN = /^[a-zA-Z0-9_]{4,25}$/;

@Component({
  selector: 'app-overview-page',
  imports: [ReactiveFormsModule, RouterLink, TranslocoPipe],
  templateUrl: './overview-page.html',
})
export class OverviewPage {
  private readonly authService = inject(AuthService);
  private readonly channelService = inject(ChannelService);
  private readonly router = inject(Router);

  protected readonly myChannels = signal<MyChannelDto[] | null>(null);
  protected readonly helixUnavailable = signal(false);
  protected readonly reauthRequired = signal(false);
  protected readonly sevenTvUnavailable = signal(false);
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
        this.reauthRequired.set(result.reauthRequired);
        this.sevenTvUnavailable.set(result.sevenTvUnavailable);
      },
      error: (error: HttpErrorResponse) => this.handleError(error),
    });

    this.channelService.listAll().subscribe({
      next: (channels) => this.adminChannels.set(channels),
      // 403 = not a global admin — the expected case for most users, not an error to surface.
      error: () => this.adminChannels.set(null),
    });
  }

  protected join(channelName: string): void {
    this.channelService.join(channelName).subscribe({
      next: () => this.openChannel(channelName),
      error: (error: HttpErrorResponse) => this.handleError(error),
    });
  }

  // Same call as join(), but stays on the overview and flips the row in place — the admin is likely
  // reactivating one of several channels, and being navigated away after each one is in the way.
  protected reactivate(channelName: string): void {
    this.channelService.join(channelName).subscribe({
      next: () => {
        this.myChannels.update((channels) =>
          channels?.map((c) => (c.channelName === channelName ? { ...c, isTracked: true, isBotActive: true } : c)) ?? null,
        );
        this.adminChannels.update((channels) =>
          channels?.map((c) => (c.channelName === channelName ? { ...c, isBotActive: true } : c)) ?? null,
        );
      },
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
