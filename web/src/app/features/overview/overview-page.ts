import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, computed, effect, inject, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { Router, RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';

import { AuthService } from '../../core/auth/auth.service';
import { MyChannelDto } from '../../core/channels/channel.model';
import { ChannelService } from '../../core/channels/channel.service';
import { GENERIC_ERROR_TRANSLATION_KEY, apiErrorTranslationKey } from '../../core/i18n/api-error';
import { WorkerHealthService } from '../../core/health/worker-health.service';
import { LIVE_EVENT_TYPES, LIVE_STATUS_URL } from '../../core/live/live-event.model';
import { liveReload } from '../../core/live/live-reload';
import { Button } from '../../shared/ui/button';
import { EmptyState } from '../../shared/ui/empty-state';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { SkeletonRows } from '../../shared/ui/skeleton-rows';
import { StatusBadge } from '../../shared/ui/status-badge';

const OVERVIEW_RELOAD_DEBOUNCE_MS = 250;
const LIVE_AGE_TICK_MS = 30_000;

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

  // rxResource instead of a one-shot constructor subscribe: live.changed pushes reload the list.
  // Two different mechanisms keep the rows on screen across such a reload: the resource itself holds
  // its previous value while it is *loading*, and lastGoodChannels below holds it across an *error* —
  // a resource drops to hasValue() === false when a reload fails, which would otherwise blank the
  // whole list because a background push happened to hit a hiccup.
  private readonly myChannelsResource = rxResource({
    stream: () => this.channelService.listMine(),
  });

  private readonly lastGoodChannels = signal<MyChannelDto[] | null>(null);

  protected readonly myChannels = computed(() =>
    this.myChannelsResource.hasValue()
      ? this.myChannelsResource.value().channels
      : this.lastGoodChannels(),
  );
  protected readonly helixUnavailable = computed(
    () => this.myChannelsResource.hasValue() && this.myChannelsResource.value().helixUnavailable,
  );
  protected readonly reauthRequired = computed(
    () => this.myChannelsResource.hasValue() && this.myChannelsResource.value().reauthRequired,
  );
  protected readonly sevenTvUnavailable = computed(
    () => this.myChannelsResource.hasValue() && this.myChannelsResource.value().sevenTvUnavailable,
  );
  private readonly livePolledAtUtc = computed(() =>
    this.myChannelsResource.hasValue() ? this.myChannelsResource.value().livePolledAtUtc : null,
  );

  // Ticking clock signal so the tooltip below ages while the page is open. Date.now() read
  // directly inside a computed() freezes at first render (rule 14) — that was a real bug.
  private readonly nowMs = signal(Date.now());

  /** Age of the live-poll data in whole minutes, for the badge tooltip. */
  protected readonly liveAgeMinutes = computed(() => {
    const polledAt = this.livePolledAtUtc();
    if (!polledAt) {
      return 0;
    }
    return Math.max(0, Math.round((this.nowMs() - new Date(polledAt).getTime()) / 60_000));
  });

  // Kept separate from the resource's own error so a failed action is not wiped out by the
  // reload that follows it, and vice versa — same reasoning as admin-channels-page.ts.
  private readonly actionError = signal<string | null>(null);

  protected readonly errorMessage = computed(() => {
    const actionError = this.actionError();
    if (actionError) {
      return actionError;
    }
    const loadError = this.myChannelsResource.error();
    if (!loadError) {
      return null;
    }
    // A non-HTTP failure (a parse error, anything the stream throws) used to render as no message
    // at all next to an empty list. It gets the same generic key an unrecognized HTTP status does.
    return loadError instanceof HttpErrorResponse
      ? apiErrorTranslationKey(loadError)
      : GENERIC_ERROR_TRANSLATION_KEY;
  });

  constructor() {
    // Mirrors every successful load — including the local patch reactivate() writes through
    // update() — so myChannels() has something to fall back to when a later reload errors out.
    effect(() => {
      if (this.myChannelsResource.hasValue()) {
        this.lastGoodChannels.set(this.myChannelsResource.value().channels);
      }
    });

    // liveReload, not liveEvents: one poll tick can flip several channels at once, and the
    // debounce collapses that burst into a single refetch.
    liveReload(LIVE_STATUS_URL, {
      accept: [LIVE_EVENT_TYPES.liveChanged],
      debounceMs: OVERVIEW_RELOAD_DEBOUNCE_MS,
    }).subscribe(() => this.myChannelsResource.reload());

    const tick = setInterval(() => this.nowMs.set(Date.now()), LIVE_AGE_TICK_MS);
    inject(DestroyRef).onDestroy(() => clearInterval(tick));
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
        // update() on a resource without a value would patch nothing and, worse, put it into a
        // value state built from undefined — the row to patch only exists if there is a value.
        if (!this.myChannelsResource.hasValue()) {
          return;
        }
        this.myChannelsResource.update((result) =>
          result
            ? {
                ...result,
                channels: result.channels.map((c) =>
                  c.channelName === channelName ? { ...c, isTracked: true, isBotActive: true } : c,
                ),
              }
            : result,
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
    this.actionError.set(apiErrorTranslationKey(error));
  }
}
