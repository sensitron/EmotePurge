import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, input } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';

import { AdminService } from '../../core/admin/admin.service';
import { apiErrorTranslationKey } from '../../core/i18n/api-error';
import { LanguageService } from '../../core/i18n/language.service';
import { toLocale } from '../../core/i18n/locale';
import { ADMIN_LIVE_URL, LIVE_EVENT_TYPES } from '../../core/live/live-event.model';
import { liveEvents } from '../../core/live/live-reload';
import { BackLink } from '../../shared/ui/back-link';
import { Button } from '../../shared/ui/button';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { SkeletonRows } from '../../shared/ui/skeleton-rows';
import { StatusBadge, StatusBadgeTone } from '../../shared/ui/status-badge';

/** Shown for an absent value, same as on the monitoring page. */
const NO_VALUE = '—';

const WORKER_STATUS_TONES: Record<string, StatusBadgeTone> = {
  healthy: 'emerald',
  degraded: 'amber',
  absent: 'red',
  starting: 'slate',
  unavailable: 'red',
  unknown: 'slate',
};

/**
 * The support drilldown behind "why isn't my channel syncing?". Two columns of truth side by side
 * and never merged: what the database recorded, and what the worker currently believes. Their
 * disagreement *is* the diagnosis — a channel with a healthy row and no roster entry stopped being
 * counted after a reconnect, and every number on the usage-stats page for it is silently stale.
 */
@Component({
  selector: 'app-admin-channel-detail-page',
  imports: [BackLink, Button, NoticeBanner, RouterLink, SkeletonRows, StatusBadge, TranslocoPipe],
  template: `
    <div class="flex flex-col gap-4">
      <!-- Outside the @if on purpose: the way out has to exist while loading and after an error,
           which is exactly when someone wants it. -->
      <app-back-link link="/admin/channels" [label]="'admin.channelDetail.back' | transloco" />

      <header class="flex flex-wrap items-center justify-between gap-3">
        <h2 class="text-lg font-semibold">#{{ channelName() }}</h2>
        <div class="flex flex-wrap items-center gap-2">
          <a
            appButton="outline"
            [routerLink]="['/channels', channelName(), 'usage-stats']"
            [title]="'admin.channelDetail.openWorkspaceTitle' | transloco"
          >
            {{ 'admin.channelDetail.openWorkspace' | transloco }}
          </a>
          <button type="button" appButton="outline" [disabled]="isLoading()" (click)="reload()">
            {{ 'admin.channelDetail.refresh' | transloco }}
          </button>
        </div>
      </header>

      @if (errorMessage(); as error) {
        <app-notice-banner variant="error">{{ error | transloco }}</app-notice-banner>
      }

      @if (showSkeleton()) {
        <app-skeleton-rows [count]="3" />
      } @else if (detail(); as data) {
        @if (verdictKey(); as verdict) {
          <app-notice-banner [variant]="verdictVariant()">
            {{ 'admin.channelDetail.verdict.' + verdict | transloco }}
          </app-notice-banner>
        }

        <!-- Database side -->
        <section class="app-card flex flex-col gap-3 p-4">
          <div class="flex flex-wrap items-center justify-between gap-3">
            <h3 class="text-base font-semibold">
              {{ 'admin.channelDetail.database.title' | transloco }}
            </h3>
            <app-status-badge [tone]="data.channel.isBotActive ? 'emerald' : 'slate'">
              {{
                (data.channel.isBotActive ? 'admin.channels.active' : 'admin.channels.inactive')
                  | transloco
              }}
            </app-status-badge>
          </div>
          <dl class="grid grid-cols-1 gap-x-6 gap-y-2 text-sm sm:grid-cols-2">
            <div class="flex justify-between gap-4 sm:block">
              <dt class="text-slate-400">
                {{ 'admin.channelDetail.database.lastSync' | transloco }}
              </dt>
              <!-- The pair this whole column exists for: a fresh sync next to an ancient inventory
                   change is a healthy channel nobody edits, not a stalled bot. -->
              <dd class="text-slate-200">{{ formatDateTime(data.channel.lastSyncedAtUtc) }}</dd>
            </div>
            <div class="flex justify-between gap-4 sm:block">
              <dt class="text-slate-400">
                {{ 'admin.channelDetail.database.lastInventoryChange' | transloco }}
              </dt>
              <dd class="text-slate-200">
                {{ formatDateTime(data.channel.lastInventoryChangeUtc) }}
              </dd>
            </div>
            <div class="flex justify-between gap-4 sm:block">
              <dt class="text-slate-400">
                {{ 'admin.channelDetail.database.emotes' | transloco }}
              </dt>
              <dd class="text-slate-200">
                {{
                  'admin.channels.stats.emotes'
                    | transloco
                      : {
                          count: data.channel.emoteCount,
                          archived: data.channel.archivedEmoteCount,
                        }
                }}
              </dd>
            </div>
            <div class="flex justify-between gap-4 sm:block">
              <dt class="text-slate-400">
                {{ 'admin.channelDetail.database.emoteSet' | transloco }}
              </dt>
              <dd class="font-mono text-xs text-slate-200">
                {{ data.channel.activeEmoteSetId ?? NO_VALUE }}
              </dd>
            </div>
            <div class="flex justify-between gap-4 sm:block">
              <dt class="text-slate-400">
                {{ 'admin.channelDetail.database.capacity' | transloco }}
              </dt>
              <dd class="text-slate-200">{{ data.channel.activeEmoteSetCapacity ?? NO_VALUE }}</dd>
            </div>
            <div class="flex justify-between gap-4 sm:block">
              <dt class="text-slate-400">
                {{ 'admin.channelDetail.database.trackedSince' | transloco }}
              </dt>
              <dd class="text-slate-200">
                {{ formatDateTime(data.channel.trackingResumedAt ?? data.channel.createdAt) }}
              </dd>
            </div>
            <div class="flex justify-between gap-4 sm:block">
              <dt class="text-slate-400">
                {{ 'admin.channelDetail.database.twitchId' | transloco }}
              </dt>
              <dd class="font-mono text-xs text-slate-200">
                {{ data.channel.twitchChannelId ?? NO_VALUE }}
              </dd>
            </div>
          </dl>
        </section>

        <!-- Worker side -->
        <section class="app-card flex flex-col gap-3 p-4">
          <div class="flex flex-wrap items-center justify-between gap-3">
            <h3 class="text-base font-semibold">
              {{ 'admin.channelDetail.worker.title' | transloco }}
            </h3>
            <app-status-badge [tone]="workerTone()">
              {{ 'admin.channelDetail.worker.status.' + workerStatusKey() | transloco }}
            </app-status-badge>
          </div>

          @if (!data.roster.available) {
            <app-notice-banner variant="warning">
              {{ 'admin.channelDetail.worker.noSnapshot' | transloco }}
            </app-notice-banner>
          } @else if (data.roster.bootRecoveryCompleted === false) {
            <app-notice-banner variant="info">
              {{ 'admin.roster.bootRecovery' | transloco }}
            </app-notice-banner>
          } @else if (!data.roster.channel) {
            <!-- The single most actionable finding this page produces: the worker published a roster
                 and this channel is not in it, so nothing is being counted for it at all. -->
            <app-notice-banner variant="warning">
              {{ 'admin.channelDetail.worker.notInRoster' | transloco }}
            </app-notice-banner>
          }

          @if (data.roster.channel; as row) {
            <dl class="grid grid-cols-1 gap-x-6 gap-y-2 text-sm sm:grid-cols-2">
              <div class="flex justify-between gap-4 sm:block">
                <dt class="text-slate-400">
                  {{ 'admin.channelDetail.worker.ircJoin' | transloco }}
                </dt>
                <dd>
                  <app-status-badge [tone]="row.ircJoinConfirmed ? 'emerald' : 'amber'">
                    {{
                      (row.ircJoinConfirmed
                        ? 'admin.channelDetail.worker.confirmed'
                        : 'admin.channelDetail.worker.unconfirmed'
                      ) | transloco
                    }}
                  </app-status-badge>
                </dd>
              </div>
              <div class="flex justify-between gap-4 sm:block">
                <dt class="text-slate-400">
                  {{ 'admin.channelDetail.worker.lastMessage' | transloco }}
                </dt>
                <!-- Null is not a failure by itself: a quiet channel produces none. Read together
                     with the join state above, which is why they sit next to each other. -->
                <dd class="text-slate-200">{{ formatDateTime(row.lastMessageUtc) }}</dd>
              </div>
              <div class="flex justify-between gap-4 sm:block">
                <dt class="text-slate-400">
                  {{ 'admin.channelDetail.worker.emoteSetSubscription' | transloco }}
                </dt>
                <dd>
                  <app-status-badge [tone]="row.sevenTvEmoteSetAcknowledged ? 'emerald' : 'amber'">
                    {{
                      (row.sevenTvEmoteSetAcknowledged
                        ? 'admin.channelDetail.worker.acknowledged'
                        : 'admin.channelDetail.worker.pending'
                      ) | transloco
                    }}
                  </app-status-badge>
                </dd>
              </div>
              <div class="flex justify-between gap-4 sm:block">
                <dt class="text-slate-400">
                  {{ 'admin.channelDetail.worker.userSubscription' | transloco }}
                </dt>
                <dd>
                  @if (!row.sevenTvUserId) {
                    <!-- No user id was ever resolved, so no user subscription is desired. Showing
                         "pending" here would name an acknowledgement that can never arrive. -->
                    <span class="text-slate-400">
                      {{ 'admin.channelDetail.worker.noUserSubscription' | transloco }}
                    </span>
                  } @else {
                    <app-status-badge [tone]="row.sevenTvUserAcknowledged ? 'emerald' : 'amber'">
                      {{
                        (row.sevenTvUserAcknowledged
                          ? 'admin.channelDetail.worker.acknowledged'
                          : 'admin.channelDetail.worker.pending'
                        ) | transloco
                      }}
                    </app-status-badge>
                  }
                </dd>
              </div>
              <div class="flex justify-between gap-4 sm:block">
                <dt class="text-slate-400">
                  {{ 'admin.channelDetail.worker.emoteSet' | transloco }}
                </dt>
                <dd class="font-mono text-xs text-slate-200">
                  {{ row.sevenTvEmoteSetId ?? NO_VALUE }}
                </dd>
              </div>
            </dl>

            @if (emoteSetMismatch()) {
              <!-- The database switched the active set and the worker is still subscribed to the old
                   one: dispatches arrive for a set nobody reads, and the channel only updates on the
                   periodic REST resync. -->
              <app-notice-banner variant="warning">
                {{ 'admin.channelDetail.worker.emoteSetMismatch' | transloco }}
              </app-notice-banner>
            }
          }

          <p class="text-xs text-slate-500">
            {{
              'admin.channelDetail.worker.snapshotAge'
                | transloco: { seconds: data.roster.ageSeconds ?? 0 }
            }}
          </p>
        </section>
      }
    </div>
  `,
})
export class AdminChannelDetailPage {
  /** Bound from the route parameter via withComponentInputBinding. */
  readonly channelName = input.required<string>();

  private readonly adminService = inject(AdminService);
  private readonly languageService = inject(LanguageService);

  // Regel 13: the required input is read inside params, never in the constructor body.
  private readonly detailResource = rxResource({
    params: () => this.channelName(),
    stream: ({ params }) => this.adminService.getChannel(params),
  });

  protected readonly NO_VALUE = NO_VALUE;

  protected readonly detail = computed(() =>
    this.detailResource.hasValue() ? this.detailResource.value() : undefined,
  );

  protected readonly isLoading = computed(() => this.detailResource.isLoading());

  /** Skeleton on the first load only — a pushed reload keeps the rendered cards on screen. */
  protected readonly showSkeleton = computed(() => this.detailResource.status() === 'loading');

  protected readonly errorMessage = computed(() => {
    const error = this.detailResource.error();
    return error instanceof HttpErrorResponse ? apiErrorTranslationKey(error) : null;
  });

  /**
   * The worker's active set and the database's disagree. Only meaningful while the worker actually
   * holds a subscription — a null set id is "no intent at all", which the roster banner already
   * covers and which is a different problem.
   */
  protected readonly emoteSetMismatch = computed(() => {
    const data = this.detail();
    const workerSet = data?.roster.channel?.sevenTvEmoteSetId;
    return (
      !!workerSet && !!data?.channel.activeEmoteSetId && workerSet !== data.channel.activeEmoteSetId
    );
  });

  protected readonly workerStatusKey = computed(() => {
    const roster = this.detail()?.roster;
    if (!roster) {
      return 'unknown';
    }
    if (!roster.available) {
      return 'unavailable';
    }
    // Boot recovery outranks everything below it: during it, absence is expected.
    if (roster.bootRecoveryCompleted === false) {
      return 'starting';
    }
    if (!roster.channel) {
      return 'absent';
    }
    return roster.channel.ircJoinConfirmed && roster.channel.sevenTvEmoteSetAcknowledged
      ? 'healthy'
      : 'degraded';
  });

  protected readonly workerTone = computed<StatusBadgeTone>(
    () => WORKER_STATUS_TONES[this.workerStatusKey()],
  );

  /**
   * The one-line answer, so the page states a conclusion instead of leaving an admin to derive it
   * from eight fields. Null while there is nothing to conclude — a healthy channel gets no banner.
   */
  protected readonly verdictKey = computed(() => {
    const data = this.detail();
    if (!data) {
      return null;
    }
    if (!data.channel.isBotActive) {
      return 'notJoined';
    }
    const status = this.workerStatusKey();
    if (status === 'unavailable' || status === 'starting') {
      return null; // Already stated by the banner inside the worker card; not a channel verdict.
    }
    if (status === 'absent') {
      return 'absentFromWorker';
    }
    if (this.emoteSetMismatch()) {
      return 'emoteSetMismatch';
    }
    return status === 'degraded' ? 'degraded' : null;
  });

  protected readonly verdictVariant = computed(() =>
    this.verdictKey() === 'notJoined' ? ('info' as const) : ('warning' as const),
  );

  constructor() {
    // Both types matter here: channel.synced moves the database column, worker.roster moves the
    // worker column, and the page shows them side by side. channel.synced is filtered down to this
    // channel — it fires on every periodic resync of every channel, and refetching one channel's
    // drilldown because a different one synced is pure noise.
    liveEvents(
      ADMIN_LIVE_URL,
      (event) =>
        event.type === LIVE_EVENT_TYPES.workerRoster ||
        (event.type === LIVE_EVENT_TYPES.channelSynced && event.channel === this.channelName()),
    ).subscribe(() => this.detailResource.reload());
  }

  protected reload(): void {
    this.detailResource.reload();
  }

  // LOCALE_ID is bootstrap-time static and cannot follow a runtime language switch — same reasoning
  // as the monitoring page.
  protected formatDateTime(iso: string | null): string {
    if (!iso) {
      return NO_VALUE;
    }
    return new Date(iso).toLocaleString(toLocale(this.languageService.lang()), {
      dateStyle: 'short',
      timeStyle: 'medium',
    });
  }
}
