import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject } from '@angular/core';
import { rxResource, takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { TranslocoPipe } from '@jsverse/transloco';
import { filter } from 'rxjs';

import { AdminService } from '../../core/admin/admin.service';
import { SevenTvConnectionStatus, WorkerConnectionStatus } from '../../core/admin/admin.model';
import { apiErrorTranslationKey } from '../../core/i18n/api-error';
import { LanguageService } from '../../core/i18n/language.service';
import { toLocale } from '../../core/i18n/locale';
import { ADMIN_LIVE_URL, LIVE_EVENT_TYPES } from '../../core/live/live-event.model';
import { LiveUpdateService } from '../../core/live/live-update.service';
import { Button } from '../../shared/ui/button';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { SkeletonRows } from '../../shared/ui/skeleton-rows';
import { StatusBadge, StatusBadgeTone } from '../../shared/ui/status-badge';

const STATUS_TONES: Record<SevenTvConnectionStatus, StatusBadgeTone> = {
  connected: 'emerald',
  stale: 'amber',
  disconnected: 'red',
  unknown: 'slate',
  disabled: 'slate',
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
  imports: [Button, NoticeBanner, SkeletonRows, StatusBadge, TranslocoPipe],
  template: `
    <div class="flex flex-col gap-4">
      <header class="flex flex-wrap items-center justify-between gap-3">
        <h2 class="text-lg font-semibold">{{ 'admin.monitoring.title' | transloco }}</h2>
        <button
          type="button"
          appButton="outline"
          [disabled]="isLoading()"
          (click)="reload()"
          [title]="'admin.monitoring.refreshTitle' | transloco"
        >
          {{ 'admin.monitoring.refresh' | transloco }}
        </button>
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
            <h3 class="text-base font-semibold">{{ 'admin.monitoring.twitch.title' | transloco }}</h3>
            <app-status-badge [tone]="toneFor(data.status)">
              {{ 'admin.monitoring.status.' + data.status | transloco }}
            </app-status-badge>
          </div>
          <dl class="grid grid-cols-1 gap-x-6 gap-y-2 text-sm sm:grid-cols-2">
            <div class="flex justify-between gap-4 sm:block">
              <dt class="text-slate-400">
                {{ 'admin.monitoring.twitch.lastMessage' | transloco }}
              </dt>
              <dd class="text-slate-200">{{ formatDateTime(data.lastMessageReceivedUtc) }}</dd>
            </div>
            <div class="flex justify-between gap-4 sm:block">
              <dt class="text-slate-400">
                {{ 'admin.monitoring.twitch.connectAttempt' | transloco }}
              </dt>
              <dd class="text-slate-200">{{ formatDateTime(data.connectAttemptedUtc) }}</dd>
            </div>
          </dl>
        </section>

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
              <span class="text-slate-400">
                {{ 'admin.monitoring.sevenTv.subscriptions' | transloco }}
              </span>
              <!-- The numbers are rendered as text as well, so the bar never carries meaning on its
                   own (it is additionally an ARIA progressbar for assistive tech). -->
              <span class="text-slate-200">
                {{ formatNumber(data.sevenTv.desiredSubscriptionCount) }} /
                {{ formatNumber(data.sevenTv.subscriptionLimit) }}
              </span>
            </div>
            <div
              class="h-2 w-full overflow-hidden rounded-full bg-slate-800"
              role="progressbar"
              [attr.aria-valuenow]="data.sevenTv.desiredSubscriptionCount"
              aria-valuemin="0"
              [attr.aria-valuemax]="data.sevenTv.subscriptionLimit"
              [attr.aria-label]="'admin.monitoring.sevenTv.utilization' | transloco"
            >
              <div
                class="h-full rounded-full bg-purple-500"
                [style.width.%]="utilizationPercent()"
              ></div>
            </div>
          </div>

          <dl class="grid grid-cols-1 gap-x-6 gap-y-2 text-sm sm:grid-cols-2">
            <div class="flex justify-between gap-4 sm:block">
              <dt class="text-slate-400">
                {{ 'admin.monitoring.sevenTv.channels' | transloco }}
              </dt>
              <dd class="text-slate-200">{{ formatNumber(data.sevenTv.desiredChannelCount) }}</dd>
            </div>
            <div class="flex justify-between gap-4 sm:block">
              <dt class="text-slate-400">
                {{ 'admin.monitoring.sevenTv.unacknowledged' | transloco }}
              </dt>
              <dd class="text-slate-200">
                @if (hasUnacknowledged()) {
                  <app-status-badge tone="amber">
                    {{ formatNumber(data.sevenTv.unacknowledgedCount) }}
                  </app-status-badge>
                } @else {
                  {{ formatNumber(data.sevenTv.unacknowledgedCount) }}
                }
              </dd>
            </div>
            <div class="flex justify-between gap-4 sm:block">
              <dt class="text-slate-400">
                {{ 'admin.monitoring.sevenTv.lastFrame' | transloco }}
              </dt>
              <dd class="text-slate-200">{{ formatDateTime(data.sevenTv.lastFrameUtc) }}</dd>
            </div>
            <div class="flex justify-between gap-4 sm:block">
              <dt class="text-slate-400">
                {{ 'admin.monitoring.sevenTv.lastDispatch' | transloco }}
              </dt>
              <dd class="text-slate-200">{{ formatDateTime(data.sevenTv.lastDispatchUtc) }}</dd>
            </div>
          </dl>
        </section>

        <!-- Batch flush -->
        <section class="app-card flex flex-col gap-3 p-4">
          <div class="flex flex-wrap items-center justify-between gap-3">
            <h3 class="text-base font-semibold">{{ 'admin.monitoring.flush.title' | transloco }}</h3>
            <app-status-badge [tone]="hasFlushFailures() ? 'red' : 'emerald'">
              {{ 'admin.monitoring.flush.consecutiveFailures' | transloco }}:
              {{ formatNumber(data.flush.consecutiveFailures) }}
            </app-status-badge>
          </div>
          <dl class="grid grid-cols-1 gap-x-6 gap-y-2 text-sm sm:grid-cols-2">
            <div class="flex justify-between gap-4 sm:block">
              <dt class="text-slate-400">{{ 'admin.monitoring.flush.lastSuccess' | transloco }}</dt>
              <dd class="text-slate-200">{{ formatDateTime(data.flush.lastSuccessUtc) }}</dd>
            </div>
            <div class="flex justify-between gap-4 sm:block">
              <dt class="text-slate-400">
                {{ 'admin.monitoring.flush.lastRowCount' | transloco }}
              </dt>
              <dd class="text-slate-200">{{ formatNumber(data.flush.lastRowCount) }}</dd>
            </div>
            <div class="flex justify-between gap-4 sm:block">
              <dt class="text-slate-400">{{ 'admin.monitoring.flush.pending' | transloco }}</dt>
              <dd class="text-slate-200">{{ formatNumber(data.flush.pendingEmoteCount) }}</dd>
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

  // No defaultValue: "no snapshot yet" and "an all-zero snapshot" must not look alike, so the
  // template branches on undefined instead of rendering a fabricated empty one.
  private readonly healthResource = rxResource({
    stream: () => this.adminService.getHealth(),
  });

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

  protected readonly hasUnacknowledged = computed(
    () => (this.health()?.sevenTv.unacknowledgedCount ?? 0) > 0,
  );

  protected readonly hasFlushFailures = computed(
    () => (this.health()?.flush.consecutiveFailures ?? 0) > 0,
  );

  constructor() {
    // No debounce: WorkerHealthPublisher writes its snapshot on a ~20 s cadence, so this can never
    // fire faster than the refetch it triggers. The URL is constant, so no toObservable indirection.
    this.liveUpdateService
      .stream(ADMIN_LIVE_URL)
      .pipe(
        filter((event) => event.type === LIVE_EVENT_TYPES.workerHealth),
        takeUntilDestroyed(),
      )
      .subscribe(() => this.healthResource.reload());
  }

  protected reload(): void {
    this.healthResource.reload();
  }

  protected toneFor(status: SevenTvConnectionStatus | WorkerConnectionStatus): StatusBadgeTone {
    return STATUS_TONES[status] ?? 'slate';
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
