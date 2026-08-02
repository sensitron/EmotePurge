import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, viewChild } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { TranslocoPipe } from '@jsverse/transloco';

import { AdminService } from '../../core/admin/admin.service';
import { SevenTvConnectionStatus, WorkerConnectionStatus } from '../../core/admin/admin.model';
import { apiErrorTranslationKey } from '../../core/i18n/api-error';
import { LanguageService } from '../../core/i18n/language.service';
import { toLocale } from '../../core/i18n/locale';
import { ADMIN_LIVE_URL, LIVE_EVENT_TYPES } from '../../core/live/live-event.model';
import { liveEvents } from '../../core/live/live-reload';
import { LiveStatus, LiveUpdateService } from '../../core/live/live-update.service';
import { Button } from '../../shared/ui/button';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { SkeletonRows } from '../../shared/ui/skeleton-rows';
import { StatusBadge, StatusBadgeTone } from '../../shared/ui/status-badge';
import { AdminRosterCard } from './admin-roster-card';

const STATUS_TONES: Record<SevenTvConnectionStatus, StatusBadgeTone> = {
  connected: 'success',
  stale: 'warning',
  disconnected: 'danger',
  unknown: 'neutral',
  disabled: 'neutral',
};

const LIVE_TONES: Record<LiveStatus, StatusBadgeTone> = {
  idle: 'neutral',
  connecting: 'warning',
  open: 'success',
  closed: 'danger',
};

/** Shown when a value is absent — an older worker's snapshot simply lacks the newer detail fields. */
const NO_VALUE = '—';

/**
 * Read-only operational view of the worker, fed by the admin-only GET /api/admin/health (Z1 split:
 * the public /api/worker/health stays minimal because every visitor polls it). One card per
 * subsystem, because they fail independently — Twitch IRC can be fine while the 7TV socket is
 * wedged, and the batch flush can be failing while both sockets look healthy.
 */
@Component({
  selector: 'app-admin-monitoring-page',
  imports: [AdminRosterCard, Button, NoticeBanner, SkeletonRows, StatusBadge, TranslocoPipe],
  template: `
    <div class="flex flex-col gap-4">
      <header class="flex flex-wrap items-center justify-between gap-3">
        <h2 class="text-lg font-semibold">{{ 'admin.monitoring.title' | transloco }}</h2>
        <div class="flex flex-wrap items-center gap-3">
          <!-- The page updates itself from the admin SSE stream, so the button next to this badge
               needs an explanation for why it is still there: when the stream is closed (revoked
               session, 503 over the stream limit, a proxy that eats text/event-stream) nothing
               refetches on its own — LiveUpdateService has no polling fallback by design, and this
               is the page an admin opens to find out. The badge says which of the two states the
               page is in instead of leaving the button looking vestigial. -->
          <app-status-badge [tone]="liveTone()">
            {{ 'admin.monitoring.live.' + liveStatus() | transloco }}
          </app-status-badge>
          <button
            type="button"
            appButton="outline"
            [disabled]="isLoading()"
            (click)="reload()"
            [title]="'admin.monitoring.refreshTitle' | transloco"
          >
            {{ 'admin.monitoring.refresh' | transloco }}
          </button>
        </div>
      </header>

      @if (errorMessage(); as error) {
        <app-notice-banner variant="error">{{ error | transloco }}</app-notice-banner>
      }

      @if (showSkeleton()) {
        <app-skeleton-rows [count]="3" />
      } @else if (health(); as data) {
        @if (!data.snapshotAvailable) {
          <app-notice-banner variant="warning">
            {{ 'admin.monitoring.noSnapshot' | transloco }}
          </app-notice-banner>
        }

        <!-- Twitch IRC -->
        <section class="app-card flex flex-col gap-3 p-4">
          <div class="flex flex-wrap items-center justify-between gap-3">
            <h3 class="text-base font-semibold">
              {{ 'admin.monitoring.twitch.title' | transloco }}
            </h3>
            <app-status-badge [tone]="toneFor(data.status)">
              {{ 'admin.monitoring.status.' + data.status | transloco }}
            </app-status-badge>
          </div>
          <dl class="grid grid-cols-1 gap-x-6 gap-y-2 text-sm sm:grid-cols-2">
            <div class="flex justify-between gap-4 sm:block">
              <dt class="text-fg-muted">
                {{ 'admin.monitoring.twitch.lastMessage' | transloco }}
              </dt>
              <dd class="text-fg-body">{{ formatDateTime(data.lastMessageReceivedUtc) }}</dd>
            </div>
            <div class="flex justify-between gap-4 sm:block">
              <dt class="text-fg-muted">
                {{ 'admin.monitoring.twitch.connectAttempt' | transloco }}
              </dt>
              <dd class="text-fg-body">{{ formatDateTime(data.connectAttemptedUtc) }}</dd>
            </div>
            <div class="flex justify-between gap-4 sm:block">
              <dt class="text-fg-muted">
                {{ 'admin.monitoring.twitch.processStarted' | transloco }}
              </dt>
              <!-- Every counter on this page resets with the process, so its start time is what
                   makes "0 failures" readable at all. -->
              <dd class="text-fg-body">{{ formatDateTime(data.worker.processStartedUtc) }}</dd>
            </div>
          </dl>
        </section>

        <!-- Roster: the worker's channels against the database's. Its own card because it is fed by
             its own endpoint on its own cadence (see AdminRosterCard). -->
        <app-admin-roster-card />

        <!-- 7TV EventAPI -->
        <section class="app-card flex flex-col gap-3 p-4">
          <div class="flex flex-wrap items-center justify-between gap-3">
            <h3 class="text-base font-semibold">
              {{ 'admin.monitoring.sevenTv.title' | transloco }}
            </h3>
            <app-status-badge [tone]="toneFor(data.sevenTv.status)">
              {{ 'admin.monitoring.status.' + data.sevenTv.status | transloco }}
            </app-status-badge>
          </div>

          <div class="flex flex-col gap-1">
            <div class="flex flex-wrap items-baseline justify-between gap-2 text-sm">
              <span class="text-fg-muted">
                {{ 'admin.monitoring.sevenTv.subscriptions' | transloco }}
              </span>
              <!-- The numbers are rendered as text as well, so the bar never carries meaning on its
                   own (it is additionally an ARIA progressbar for assistive tech). -->
              <span class="text-fg-body">
                {{ formatNumber(data.sevenTv.desiredSubscriptionCount) }} /
                {{ formatNumber(data.sevenTv.subscriptionLimit) }}
              </span>
            </div>
            <div
              class="h-2 w-full overflow-hidden rounded-full bg-surface-inset"
              role="progressbar"
              [attr.aria-valuenow]="data.sevenTv.desiredSubscriptionCount"
              aria-valuemin="0"
              [attr.aria-valuemax]="data.sevenTv.subscriptionLimit"
              [attr.aria-label]="'admin.monitoring.sevenTv.utilization' | transloco"
            >
              <div
                class="h-full rounded-full bg-accent"
                [style.width.%]="utilizationPercent()"
              ></div>
            </div>
          </div>

          <dl class="grid grid-cols-1 gap-x-6 gap-y-2 text-sm sm:grid-cols-2">
            <div class="flex justify-between gap-4 sm:block">
              <dt class="text-fg-muted">
                {{ 'admin.monitoring.sevenTv.channels' | transloco }}
              </dt>
              <dd class="text-fg-body">{{ formatNumber(data.sevenTv.desiredChannelCount) }}</dd>
            </div>
            <div class="flex justify-between gap-4 sm:block">
              <dt class="text-fg-muted">
                {{ 'admin.monitoring.sevenTv.unacknowledged' | transloco }}
              </dt>
              <dd class="text-fg-body">
                @if (hasUnacknowledged()) {
                  <app-status-badge tone="warning">
                    {{ formatNumber(data.sevenTv.unacknowledgedCount) }}
                  </app-status-badge>
                } @else {
                  {{ formatNumber(data.sevenTv.unacknowledgedCount) }}
                }
              </dd>
            </div>
            <div class="flex justify-between gap-4 sm:block">
              <dt class="text-fg-muted">
                {{ 'admin.monitoring.sevenTv.lastFrame' | transloco }}
              </dt>
              <dd class="text-fg-body">{{ formatDateTime(data.sevenTv.lastFrameUtc) }}</dd>
            </div>
            <div class="flex justify-between gap-4 sm:block">
              <dt class="text-fg-muted">
                {{ 'admin.monitoring.sevenTv.lastDispatch' | transloco }}
              </dt>
              <dd class="text-fg-body">{{ formatDateTime(data.sevenTv.lastDispatchUtc) }}</dd>
            </div>
            @if (restRequestRate(); as rate) {
              <div class="flex justify-between gap-4 sm:block">
                <dt class="text-fg-muted">
                  {{ 'admin.monitoring.sevenTv.restRate' | transloco }}
                </dt>
                <!-- Deliberately not a utilization bar: 7TV publishes no REST quota, so there is no
                     honest denominator. A rate is what we actually know. -->
                <dd class="text-fg-body">
                  {{ 'admin.monitoring.sevenTv.restRateValue' | transloco: rate }}
                </dd>
              </div>
            }
          </dl>
        </section>

        <!-- Batch flush -->
        <section class="app-card flex flex-col gap-3 p-4">
          <div class="flex flex-wrap items-center justify-between gap-3">
            <h3 class="text-base font-semibold">
              {{ 'admin.monitoring.flush.title' | transloco }}
            </h3>
            <app-status-badge [tone]="hasFlushFailures() ? 'danger' : 'success'">
              {{ 'admin.monitoring.flush.consecutiveFailures' | transloco }}:
              {{ formatNumber(data.flush.consecutiveFailures) }}
            </app-status-badge>
          </div>
          <dl class="grid grid-cols-1 gap-x-6 gap-y-2 text-sm sm:grid-cols-2">
            <div class="flex justify-between gap-4 sm:block">
              <dt class="text-fg-muted">{{ 'admin.monitoring.flush.lastSuccess' | transloco }}</dt>
              <dd class="text-fg-body">{{ formatDateTime(data.flush.lastSuccessUtc) }}</dd>
            </div>
            <div class="flex justify-between gap-4 sm:block">
              <dt class="text-fg-muted">
                {{ 'admin.monitoring.flush.lastRowCount' | transloco }}
              </dt>
              <dd class="text-fg-body">{{ formatNumber(data.flush.lastRowCount) }}</dd>
            </div>
            <div class="flex justify-between gap-4 sm:block">
              <dt class="text-fg-muted">{{ 'admin.monitoring.flush.pending' | transloco }}</dt>
              <dd class="text-fg-body">{{ formatNumber(data.flush.pendingEmoteCount) }}</dd>
            </div>
          </dl>
        </section>
      }
    </div>
  `,
})
export class AdminMonitoringPage {
  private readonly adminService = inject(AdminService);
  private readonly languageService = inject(LanguageService);
  private readonly liveUpdateService = inject(LiveUpdateService);

  // The roster lives in its own card on its own endpoint and its own cadence, so refreshing it is
  // its own call — the refresh button used to reload the health snapshot only, which left the one
  // card an admin comes here for (which channels is the worker actually in?) reachable by nothing
  // but the 60 s worker.roster push.
  private readonly rosterCard = viewChild(AdminRosterCard);

  // No defaultValue: "no snapshot yet" and "an all-zero snapshot" must not look alike, so the
  // template branches on undefined instead of rendering a fabricated empty one.
  private readonly healthResource = rxResource({
    stream: () => this.adminService.getHealth(),
  });

  // Service-wide, so with two concurrent streams the last writer wins — harmless here because both
  // subscriptions on this page point at the same endpoint and therefore share a fate.
  protected readonly liveStatus = this.liveUpdateService.status;
  protected readonly liveTone = computed<StatusBadgeTone>(() => LIVE_TONES[this.liveStatus()]);

  // value() throws once the resource is in its error state, so it is only ever read behind
  // hasValue() — the error banner below renders from error() instead.
  protected readonly health = computed(() =>
    this.healthResource.hasValue() ? this.healthResource.value() : undefined,
  );

  /** Drives the refresh button's disabled state only — never a content swap. */
  protected readonly isLoading = computed(() => this.healthResource.isLoading());

  // Skeleton on the *first* load only. Every later load is a reload (status 'reloading', see
  // Angular's ResourceStatus), and the worker pushes `worker.health` about every 20 s: swapping the
  // rendered cards for a skeleton that often made the whole page twitch on its own. A reload keeps
  // the previous snapshot on screen and replaces it in place when the new one arrives.
  protected readonly showSkeleton = computed(() => this.healthResource.status() === 'loading');

  protected readonly errorMessage = computed(() => {
    const error = this.healthResource.error();
    return error instanceof HttpErrorResponse ? apiErrorTranslationKey(error) : null;
  });

  /** Capped at 100 so a future over-limit state can't overflow the bar out of its track. */
  protected readonly utilizationPercent = computed(() => {
    const sevenTv = this.health()?.sevenTv;
    if (!sevenTv?.desiredSubscriptionCount || !sevenTv.subscriptionLimit) {
      return 0;
    }
    return Math.min(100, (sevenTv.desiredSubscriptionCount / sevenTv.subscriptionLimit) * 100);
  });

  /**
   * The periodic resync issues one 7TV REST call per tracked channel per tick, so the load we put on
   * a third party is a rate, not a share of a quota — 7TV publishes no limit to divide by. Null
   * until both figures are known; a fabricated interval would state a rate we never measured.
   */
  protected readonly restRequestRate = computed(() => {
    const sevenTv = this.health()?.sevenTv;
    const channels = sevenTv?.desiredChannelCount;
    const intervalSeconds = sevenTv?.resyncIntervalSeconds;
    if (!channels || !intervalSeconds) {
      return null;
    }
    return {
      requests: channels,
      seconds: intervalSeconds,
      perSecond: (channels / intervalSeconds).toFixed(2),
    };
  });

  protected readonly hasUnacknowledged = computed(
    () => (this.health()?.sevenTv.unacknowledgedCount ?? 0) > 0,
  );

  protected readonly hasFlushFailures = computed(
    () => (this.health()?.flush.consecutiveFailures ?? 0) > 0,
  );

  constructor() {
    // No debounce: WorkerHealthPublisher writes its snapshot on a ~20 s cadence, so this can never
    // fire faster than the refetch it triggers. The URL is constant, so no toObservable indirection.
    liveEvents(ADMIN_LIVE_URL, [LIVE_EVENT_TYPES.workerHealth]).subscribe(() =>
      this.healthResource.reload(),
    );
  }

  // Undefined while the first load still shows the skeleton — the roster card renders inside the
  // health branch, so there is nothing to reload yet and nothing to do about it.
  protected reload(): void {
    this.healthResource.reload();
    this.rosterCard()?.reload();
  }

  protected toneFor(status: SevenTvConnectionStatus | WorkerConnectionStatus): StatusBadgeTone {
    return STATUS_TONES[status] ?? 'neutral';
  }

  // LOCALE_ID is bootstrap-time static and can't follow a runtime language switch, so dates and
  // numbers go through toLocale() — same reasoning as vote-session-detail-page.ts.
  protected formatDateTime(iso: string | null): string {
    if (!iso) {
      return NO_VALUE;
    }
    return new Date(iso).toLocaleString(toLocale(this.languageService.lang()), {
      dateStyle: 'short',
      timeStyle: 'medium',
    });
  }

  protected formatNumber(value: number | null): string {
    if (value === null || value === undefined) {
      return NO_VALUE;
    }
    return new Intl.NumberFormat(toLocale(this.languageService.lang())).format(value);
  }
}
