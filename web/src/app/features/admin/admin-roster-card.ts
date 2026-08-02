import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';

import { AdminService } from '../../core/admin/admin.service';
import { apiErrorTranslationKey } from '../../core/i18n/api-error';
import { ADMIN_LIVE_URL, LIVE_EVENT_TYPES } from '../../core/live/live-event.model';
import { liveEvents } from '../../core/live/live-reload';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { StatusBadge, StatusBadgeTone } from '../../shared/ui/status-badge';

/** Three times the worker's 60-second roster cadence — the same rule the Redis TTL follows. */
const STALE_AFTER_SECONDS = 180;

const STATUS_TONES: Record<string, StatusBadgeTone> = {
  complete: 'emerald',
  incomplete: 'amber',
  starting: 'slate',
  stale: 'amber',
  unavailable: 'red',
  unknown: 'slate',
};

/**
 * Soll/Ist for the worker's channel roster: what the database says the bot should be in, against
 * what the worker itself reports being in. A channel that dropped out of IRC after a reconnect
 * counts nothing and says nothing — this card is the only place that discrepancy becomes visible
 * without SSH.
 *
 * Its own resource rather than fields on the monitoring page's health call: the roster is published
 * every 60 s, the health snapshot every 20 s, and folding them together would refetch this three
 * times per change.
 */
@Component({
  selector: 'app-admin-roster-card',
  imports: [NoticeBanner, RouterLink, StatusBadge, TranslocoPipe],
  template: `
    <section class="app-card flex flex-col gap-3 p-4">
      <div class="flex flex-wrap items-center justify-between gap-3">
        <h3 class="text-base font-semibold">{{ 'admin.roster.title' | transloco }}</h3>
        <app-status-badge [tone]="tone()">
          {{ 'admin.roster.status.' + statusKey() | transloco }}
        </app-status-badge>
      </div>

      @if (errorMessage(); as error) {
        <app-notice-banner variant="error">{{ error | transloco }}</app-notice-banner>
      } @else if (roster(); as data) {
        @if (!data.snapshotAvailable) {
          <app-notice-banner variant="warning">
            {{ 'admin.roster.noSnapshot' | transloco }}
          </app-notice-banner>
        } @else {
          @if (data.bootRecoveryCompleted === false) {
            <!-- The single most important caveat on this card: _desiredChannels is empty until boot
                 recovery has rejoined everything, so right after a deploy every channel is
                 legitimately "missing". Without saying so, the card cries wolf on every redeploy. -->
            <app-notice-banner variant="info">
              {{ 'admin.roster.bootRecovery' | transloco }}
            </app-notice-banner>
          }
          @if (isStale()) {
            <app-notice-banner variant="warning">
              {{ 'admin.roster.stale' | transloco: { seconds: data.ageSeconds } }}
            </app-notice-banner>
          }
          @if (data.truncated) {
            <app-notice-banner variant="warning">
              {{ 'admin.roster.truncated' | transloco }}
            </app-notice-banner>
          }

          <dl class="grid grid-cols-1 gap-x-6 gap-y-2 text-sm sm:grid-cols-2">
            <div class="flex justify-between gap-4 sm:block">
              <dt class="text-fg-muted">{{ 'admin.roster.tracked' | transloco }}</dt>
              <dd class="text-fg-body">{{ data.trackedChannelCount }}</dd>
            </div>
            <div class="flex justify-between gap-4 sm:block">
              <dt class="text-fg-muted">{{ 'admin.roster.ircConfirmed' | transloco }}</dt>
              <dd class="text-fg-body">
                {{ data.ircConfirmedCount }} / {{ data.trackedChannelCount }}
              </dd>
            </div>
            <div class="flex justify-between gap-4 sm:block">
              <dt class="text-fg-muted">{{ 'admin.roster.sevenTvAcknowledged' | transloco }}</dt>
              <dd class="text-fg-body">
                {{ data.sevenTvAcknowledgedCount }} / {{ data.trackedChannelCount }}
              </dd>
            </div>
            <div class="flex justify-between gap-4 sm:block">
              <dt class="text-fg-muted">{{ 'admin.roster.worker' | transloco }}</dt>
              <dd class="text-fg-body">{{ data.workerInstanceId }}</dd>
            </div>
          </dl>

          <!-- Twitch's join headroom. Two numbers, separately labelled with where each comes from:
               one is Twitch's rule, the other is ours. Merging them into a single "capacity" would
               state our operating decision as if Twitch had made it. -->
          <div class="flex flex-col gap-1">
            <div class="flex flex-wrap items-baseline justify-between gap-2 text-sm">
              <span class="text-fg-muted">{{ 'admin.roster.joinBudget' | transloco }}</span>
              <span [class]="overBudget() ? 'text-warning-fg' : 'text-fg-body'">
                {{ data.trackedChannelCount }} / {{ data.ceilings.twitchJoinBudgetChannels }}
              </span>
            </div>
            <div
              class="h-2 w-full overflow-hidden rounded-full bg-surface-inset"
              role="progressbar"
              [attr.aria-valuenow]="data.trackedChannelCount"
              aria-valuemin="0"
              [attr.aria-valuemax]="data.ceilings.twitchJoinBudgetChannels"
              [attr.aria-label]="'admin.roster.joinBudget' | transloco"
            >
              <div
                class="h-full rounded-full"
                [class]="overBudget() ? 'bg-warning-dot' : 'bg-accent'"
                [style.width.%]="budgetPercent()"
              ></div>
            </div>
            <p class="text-xs text-fg-muted">
              {{ 'admin.roster.joinBudgetHint' | transloco }}
            </p>
            <p class="text-xs text-fg-muted">
              {{
                'admin.roster.twitchLimitHint'
                  | transloco: { limit: data.ceilings.twitchConcurrentChannelLimit }
              }}
            </p>
          </div>

          @if (missingFromIrc().length > 0) {
            <div class="flex flex-col gap-1">
              <p class="text-sm text-warning-fg">
                {{ 'admin.roster.missingIrc' | transloco: { count: data.missingFromIrcTotal } }}
              </p>
              <ul class="flex flex-wrap gap-x-3 gap-y-1 text-sm">
                @for (name of missingFromIrc(); track name) {
                  <li>
                    <a
                      [routerLink]="['/admin/channels', name]"
                      class="text-accent-fg underline-offset-2 hover:underline"
                    >
                      #{{ name }}
                    </a>
                  </li>
                }
              </ul>
            </div>
          }

          @if (missingFromSevenTv().length > 0) {
            <div class="flex flex-col gap-1">
              <p class="text-sm text-warning-fg">
                {{
                  'admin.roster.missingSevenTv' | transloco: { count: data.missingFromSevenTvTotal }
                }}
              </p>
              <ul class="flex flex-wrap gap-x-3 gap-y-1 text-sm">
                @for (name of missingFromSevenTv(); track name) {
                  <li>
                    <a
                      [routerLink]="['/admin/channels', name]"
                      class="text-accent-fg underline-offset-2 hover:underline"
                    >
                      #{{ name }}
                    </a>
                  </li>
                }
              </ul>
            </div>
          }

          @if (unknownToDatabase().length > 0) {
            <div class="flex flex-col gap-1">
              <p class="text-sm text-warning-fg">
                {{
                  'admin.roster.unknownToDatabase'
                    | transloco: { count: data.unknownToDatabaseTotal }
                }}
              </p>
              <ul class="flex flex-wrap gap-x-3 gap-y-1 text-sm text-fg-secondary">
                @for (name of unknownToDatabase(); track name) {
                  <li>#{{ name }}</li>
                }
              </ul>
            </div>
          }
        }
      }
    </section>
  `,
})
export class AdminRosterCard {
  private readonly adminService = inject(AdminService);

  // No defaultValue, same reasoning as the health resource: "not loaded yet" and "an all-zero
  // roster" are different states and must not render alike.
  private readonly rosterResource = rxResource({
    stream: () => this.adminService.getRoster(),
  });

  protected readonly roster = computed(() =>
    this.rosterResource.hasValue() ? this.rosterResource.value() : undefined,
  );

  protected readonly errorMessage = computed(() => {
    const error = this.rosterResource.error();
    return error instanceof HttpErrorResponse ? apiErrorTranslationKey(error) : null;
  });

  /** Older than three publish intervals — at which point the TTL is about to drop the key anyway. */
  protected readonly isStale = computed(
    () => (this.roster()?.ageSeconds ?? 0) > STALE_AFTER_SECONDS,
  );

  protected readonly overBudget = computed(() => {
    const data = this.roster();
    return !!data && data.trackedChannelCount > data.ceilings.twitchJoinBudgetChannels;
  });

  /** Capped so an over-budget channel count cannot overflow the bar out of its track. */
  protected readonly budgetPercent = computed(() => {
    const data = this.roster();
    if (!data?.ceilings.twitchJoinBudgetChannels) {
      return 0;
    }
    return Math.min(100, (data.trackedChannelCount / data.ceilings.twitchJoinBudgetChannels) * 100);
  });

  protected readonly missingFromIrc = computed(() => this.roster()?.missingFromIrc ?? []);
  protected readonly missingFromSevenTv = computed(() => this.roster()?.missingFromSevenTv ?? []);
  protected readonly unknownToDatabase = computed(() => this.roster()?.unknownToDatabase ?? []);

  protected readonly statusKey = computed(() => {
    const data = this.roster();
    if (!data) {
      return 'unknown';
    }
    if (!data.snapshotAvailable) {
      return 'unavailable';
    }
    // Boot recovery outranks every deficit: during it the deficits are expected, and reporting them
    // as a problem would train an admin to ignore this badge after every deploy.
    if (data.bootRecoveryCompleted === false) {
      return 'starting';
    }
    if (this.isStale()) {
      return 'stale';
    }
    const complete =
      data.ircConfirmedCount === data.trackedChannelCount &&
      data.sevenTvAcknowledgedCount === data.trackedChannelCount &&
      (data.unknownToDatabaseTotal ?? 0) === 0;
    return complete ? 'complete' : 'incomplete';
  });

  protected readonly tone = computed<StatusBadgeTone>(() => STATUS_TONES[this.statusKey()]);

  constructor() {
    // No debounce: the publisher writes once a minute, so this can never outpace the refetch.
    liveEvents(ADMIN_LIVE_URL, [LIVE_EVENT_TYPES.workerRoster]).subscribe(() =>
      this.rosterResource.reload(),
    );
  }

  reload(): void {
    this.rosterResource.reload();
  }
}
