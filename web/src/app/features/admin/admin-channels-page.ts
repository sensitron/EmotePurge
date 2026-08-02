import { Dialog } from '@angular/cdk/dialog';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { Observable } from 'rxjs';

import { AdminChannel } from '../../core/admin/admin.model';
import { AdminService } from '../../core/admin/admin.service';
import { channelNameValidator, normalizeChannelName } from '../../core/channels/channel-name';
import { ChannelService } from '../../core/channels/channel.service';
import { apiErrorTranslationKey } from '../../core/i18n/api-error';
import { LanguageService } from '../../core/i18n/language.service';
import { toLocale } from '../../core/i18n/locale';
import { ADMIN_LIVE_URL, LIVE_EVENT_TYPES } from '../../core/live/live-event.model';
import { liveEvents } from '../../core/live/live-reload';
import { Button } from '../../shared/ui/button';
import { EmptyState } from '../../shared/ui/empty-state';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { SkeletonRows } from '../../shared/ui/skeleton-rows';
import { StatusBadge } from '../../shared/ui/status-badge';
import { TypedConfirmDialog, TypedConfirmDialogData } from '../../shared/ui/typed-confirm-dialog';

const NO_CHANNELS: AdminChannel[] = [];

/** Shown for a channel that has never been synced — distinct from a date, on purpose. */
const NO_VALUE = '—';

/** How long the "resync queued" hint stays on the row. Longer than the copy-link feedback (2 s)
 *  because it confirms a request that left the browser, not a local clipboard write. */
const RESYNC_FEEDBACK_MS = 4000;

const RESYNC_QUEUED_KEY = 'admin.channels.resync.queued';
const RESYNC_COMPLETED_KEY = 'admin.channels.resync.completed';

/**
 * Every tracked channel with its aggregates, plus the three write actions an admin has over one:
 * join, leave, and — new here — purge. The purge endpoint existed without any UI on purpose
 * (Review S1-1); that call is reversed for this page (see DECISIONS 2026-07-31), because the
 * alternative was an admin hand-crafting a DELETE against production. It is guarded three ways: the
 * admin-only route, the server-side GlobalAdminAuthorizationFilter, and a typed name confirmation.
 *
 * Rows follow the stretched-link contract like the overview's "my channels" cards: the whole card
 * opens the channel, while the action buttons stay independently clickable via the elevated
 * (`relative z-10`) actions container.
 */
@Component({
  selector: 'app-admin-channels-page',
  imports: [
    Button,
    EmptyState,
    NoticeBanner,
    ReactiveFormsModule,
    RouterLink,
    SkeletonRows,
    StatusBadge,
    TranslocoPipe,
  ],
  template: `
    <div class="flex flex-col gap-4">
      <header class="flex flex-wrap items-center justify-between gap-3">
        <h2 class="text-lg font-semibold">{{ 'admin.channels.title' | transloco }}</h2>
        <button
          type="button"
          appButton="outline"
          [disabled]="isLoading()"
          (click)="reload()"
          [title]="'admin.channels.refreshTitle' | transloco"
        >
          {{ 'admin.channels.refresh' | transloco }}
        </button>
      </header>

      <!-- Same capability as the overview's admin section, offered here as well: this is the page an
           admin is on when they realize a channel is missing. -->
      <form class="flex flex-wrap gap-2" (submit)="onJoinSubmit($event)">
        <input
          id="admin-join-channel-name"
          type="text"
          [formControl]="channelNameControl"
          [placeholder]="'admin.channels.joinPlaceholder' | transloco"
          [attr.aria-label]="'admin.channels.joinPlaceholder' | transloco"
          [attr.aria-invalid]="
            channelNameControl.invalid && channelNameControl.touched ? 'true' : null
          "
          [attr.aria-describedby]="
            channelNameControl.invalid && channelNameControl.touched
              ? 'admin-join-channel-name-error'
              : null
          "
          class="app-input flex-1"
        />
        <button type="submit" appButton="primary" buttonSize="lg">
          {{ 'admin.channels.joinChannel' | transloco }}
        </button>
      </form>
      @if (channelNameControl.invalid && channelNameControl.touched) {
        <p id="admin-join-channel-name-error" class="text-sm text-danger-fg">
          {{ 'admin.channels.invalidChannelName' | transloco }}
        </p>
      }

      @if (errorMessage(); as error) {
        <app-notice-banner variant="error">{{ error | transloco }}</app-notice-banner>
      }

      @if (showSkeleton()) {
        <app-skeleton-rows [count]="3" />
      } @else if (channels().length === 0) {
        <app-empty-state
          icon="📺"
          [title]="'admin.channels.empty' | transloco"
          [description]="'admin.channels.emptyHint' | transloco"
        />
      } @else {
        <ul class="flex flex-col gap-2">
          @for (channel of channels(); track channel.channelName) {
            <li class="app-card app-card-interactive relative flex flex-col gap-2 px-4 py-3">
              <div class="flex flex-wrap items-center gap-x-3 gap-y-2">
                <!-- The stretched link goes to the admin drilldown, not to the channel workspace:
                     from this list the next question is almost always "what is wrong with it", and
                     the workspace stays one deliberate click away inside the drilldown. -->
                <a
                  [routerLink]="['/admin/channels', channel.channelName]"
                  class="app-card-link max-w-full truncate font-medium text-fg"
                >
                  #{{ channel.channelName }}
                </a>
                <app-status-badge [tone]="channel.isBotActive ? 'emerald' : 'slate'">
                  {{
                    (channel.isBotActive ? 'admin.channels.active' : 'admin.channels.inactive')
                      | transloco
                  }}
                </app-status-badge>

                <div class="relative z-10 ml-auto flex flex-wrap items-center justify-end gap-2">
                  @if (resyncFeedback() === channel.channelName) {
                    <!-- Transient inline confirmation, same pattern as the vote-session list's copy
                         feedback: a 202 only means "queued". The live stream upgrades the wording to
                         "completed" once the worker actually reports the sync back. -->
                    <span role="status" class="text-xs text-success-fg">
                      {{ resyncFeedbackKey() | transloco }}
                    </span>
                  }
                  @if (!channel.isBotActive) {
                    <button
                      type="button"
                      appButton="neutral"
                      [disabled]="pendingChannel() === channel.channelName"
                      (click)="join(channel.channelName)"
                    >
                      {{ 'admin.channels.actions.join' | transloco }}
                    </button>
                  } @else {
                    <!-- Resync only while the bot is in the channel: the worker resolves the command
                         against its joined channels, so offering it on an inactive row would only
                         ever produce a 409. -->
                    <button
                      type="button"
                      appButton="outline"
                      [disabled]="pendingChannel() === channel.channelName"
                      (click)="resync(channel.channelName)"
                    >
                      {{ 'admin.channels.actions.resync' | transloco }}
                    </button>
                    <button
                      type="button"
                      appButton="outline"
                      [disabled]="pendingChannel() === channel.channelName"
                      (click)="leave(channel.channelName)"
                    >
                      {{ 'admin.channels.actions.leave' | transloco }}
                    </button>
                  }
                  <button
                    type="button"
                    appButton="danger"
                    [disabled]="pendingChannel() === channel.channelName"
                    (click)="confirmPurge(channel)"
                  >
                    {{ 'admin.channels.actions.purge' | transloco }}
                  </button>
                </div>
              </div>

              <p class="flex flex-wrap gap-x-2 gap-y-1 text-xs text-fg-muted">
                <span>
                  {{
                    'admin.channels.stats.emotes'
                      | transloco
                        : {
                            count: formatNumber(channel.emoteCount),
                            archived: formatNumber(channel.archivedEmoteCount),
                          }
                  }}
                </span>
                <span aria-hidden="true">·</span>
                <span>
                  {{
                    'admin.channels.stats.voteSessions'
                      | transloco
                        : {
                            count: formatNumber(channel.voteSessionCount),
                            active: formatNumber(channel.activeVoteSessionCount),
                          }
                  }}
                </span>
                <span aria-hidden="true">·</span>
                <span>
                  {{
                    'admin.channels.stats.created'
                      | transloco: { date: formatDateTime(channel.createdAt) }
                  }}
                </span>
                <span aria-hidden="true">·</span>
                <span>
                  {{
                    'admin.channels.stats.lastSync'
                      | transloco: { date: formatDateTime(channel.lastSyncedAtUtc) }
                  }}
                </span>
              </p>
            </li>
          }
        </ul>
      }
    </div>
  `,
})
export class AdminChannelsPage {
  private readonly adminService = inject(AdminService);
  private readonly channelService = inject(ChannelService);
  private readonly dialog = inject(Dialog);
  private readonly languageService = inject(LanguageService);
  private readonly translocoService = inject(TranslocoService);

  private readonly channelsResource = rxResource({
    stream: () => this.adminService.listChannels(),
    defaultValue: NO_CHANNELS,
  });

  // value() throws once the resource is in its error state, so it is only ever read behind
  // hasValue() — the error banner renders from error() instead.
  protected readonly channels = computed(() =>
    this.channelsResource.hasValue() ? this.channelsResource.value() : NO_CHANNELS,
  );

  /** Drives the refresh button's disabled state only — never a content swap. */
  protected readonly isLoading = computed(() => this.channelsResource.isLoading());

  // Skeleton on the *first* load only — same reasoning as admin-monitoring-page.ts. Here the push
  // is `channel.synced`, which arrives on every periodic 7TV resync, so the list would blank out
  // under an admin's cursor without this. Note that hasValue() cannot carry this decision: the
  // resource has a defaultValue, so it reports a value from the very first frame.
  protected readonly showSkeleton = computed(() => this.channelsResource.status() === 'loading');

  /** Blocks a second click on the row an action is already running against — a double-fired purge
   *  would otherwise come back as a 404 and read as an error the admin did not cause. */
  protected readonly pendingChannel = signal<string | null>(null);

  /** Name of the channel whose resync was just accepted, or null. A 202 changes nothing visible on
   *  the row, so this transient hint is the only proof the click did anything. */
  protected readonly resyncFeedback = signal<string | null>(null);

  /** Which wording the hint currently shows: "queued" right after the 202, upgraded to "completed"
   *  when the worker's `channel.synced` push for that very channel arrives. */
  protected readonly resyncFeedbackKey = signal(RESYNC_QUEUED_KEY);
  private resyncFeedbackTimeout?: ReturnType<typeof setTimeout>;

  // Kept separate from the resource's own error so a failed action is not wiped out by the reload
  // that follows it, and vice versa — same reasoning as vote-session-list-page.ts.
  private readonly actionError = signal<string | null>(null);

  protected readonly errorMessage = computed(() => {
    const actionError = this.actionError();
    if (actionError) {
      return actionError;
    }
    const loadError = this.channelsResource.error();
    return loadError instanceof HttpErrorResponse ? apiErrorTranslationKey(loadError) : null;
  });

  // Validates the *normalized* value (Regel 9), so an admin can paste "HandOfBlood" the way Twitch
  // displays it — the server normalizes before matching its own lower-case-only pattern too.
  protected readonly channelNameControl = new FormControl('', {
    nonNullable: true,
    validators: [channelNameValidator],
  });

  constructor() {
    // A sync finished somewhere: the aggregates on every row can have moved (emote/archived counts,
    // lastSyncedAtUtc), so reload unconditionally. If it is the channel this admin just resynced,
    // the hint stops guessing and states the fact — that is what replaces the setTimeout as the
    // source of truth. The timeout stays as the cleanup fallback for when no push ever arrives.
    // liveEvents, not liveReload: the hint below needs the individual event's `channel`, which a
    // merged burst would flatten away — and syncs are far too rare to burst in the first place.
    liveEvents(ADMIN_LIVE_URL, [LIVE_EVENT_TYPES.channelSynced]).subscribe((event) => {
      this.channelsResource.reload();
      if (event.channel && event.channel === this.resyncFeedback()) {
        this.showResyncFeedback(event.channel, RESYNC_COMPLETED_KEY);
      }
    });
  }

  protected reload(): void {
    this.channelsResource.reload();
  }

  protected onJoinSubmit(event: Event): void {
    event.preventDefault();

    if (this.channelNameControl.invalid) {
      this.channelNameControl.markAsTouched();
      return;
    }

    const channelName = normalizeChannelName(this.channelNameControl.value);
    this.channelNameControl.reset('');
    this.join(channelName);
  }

  protected join(channelName: string): void {
    this.runAction(channelName, () => this.channelService.join(channelName));
  }

  protected leave(channelName: string): void {
    this.runAction(channelName, () => this.channelService.leave(channelName));
  }

  /** Not routed through `runAction`: that one reloads the list afterwards, which would be wrong
   *  here — the worker has only accepted the command, so every aggregate (`lastSyncedAtUtc` above
   *  all) is still the pre-resync value at this point. */
  protected resync(channelName: string): void {
    this.actionError.set(null);
    this.pendingChannel.set(channelName);
    this.adminService.resyncChannel(channelName).subscribe({
      next: () => {
        this.pendingChannel.set(null);
        this.showResyncFeedback(channelName, RESYNC_QUEUED_KEY);
      },
      error: (error: HttpErrorResponse) => {
        this.pendingChannel.set(null);
        this.actionError.set(apiErrorTranslationKey(error));
      },
    });
  }

  protected confirmPurge(channel: AdminChannel): void {
    // The message names what disappears with the channel, in counts taken from this very row: an
    // admin should not have to remember that the cascade reaches usage stats and votes.
    const data: TypedConfirmDialogData = {
      title: this.translocoService.translate('admin.channels.purge.title'),
      message: this.translocoService.translate('admin.channels.purge.message', {
        channel: channel.channelName,
        emotes: this.formatNumber(channel.emoteCount),
        voteSessions: this.formatNumber(channel.voteSessionCount),
      }),
      requiredText: channel.channelName,
      inputLabel: this.translocoService.translate('admin.channels.purge.inputLabel'),
      confirmLabel: this.translocoService.translate('admin.channels.purge.confirm'),
    };

    this.dialog
      .open<boolean>(TypedConfirmDialog, {
        data,
        backdropClass: 'app-dialog-backdrop',
        panelClass: 'app-dialog-panel',
        // Names the dialog for assistive tech (same wiring as DeleteConfirmDialog's call site).
        ariaLabelledBy: 'typed-confirm-dialog-title',
      })
      .closed.subscribe((confirmed) => {
        if (!confirmed) {
          return;
        }
        this.runAction(channel.channelName, () => this.channelService.purge(channel.channelName));
      });
  }

  // LOCALE_ID is bootstrap-time static and cannot follow a runtime language switch, so dates and
  // numbers go through toLocale() — same as admin-monitoring-page.ts.
  protected formatDateTime(iso: string | null): string {
    if (!iso) {
      return NO_VALUE;
    }
    return new Date(iso).toLocaleString(toLocale(this.languageService.lang()), {
      dateStyle: 'short',
      timeStyle: 'short',
    });
  }

  protected formatNumber(value: number): string {
    return new Intl.NumberFormat(toLocale(this.languageService.lang())).format(value);
  }

  /** Shows the transient hint on `channelName` and restarts its removal timer — so the upgrade to
   *  "completed" gets its own full display window instead of inheriting the queued one's remainder. */
  private showResyncFeedback(channelName: string, messageKey: string): void {
    clearTimeout(this.resyncFeedbackTimeout);
    this.resyncFeedback.set(channelName);
    this.resyncFeedbackKey.set(messageKey);
    this.resyncFeedbackTimeout = setTimeout(
      () => this.resyncFeedback.set(null),
      RESYNC_FEEDBACK_MS,
    );
  }

  private runAction(channelName: string, action: () => Observable<unknown>): void {
    this.actionError.set(null);
    this.pendingChannel.set(channelName);
    action().subscribe({
      next: () => {
        this.pendingChannel.set(null);
        // Always a full reload rather than a local patch: join/leave/purge each change the
        // aggregates (and purge removes the row entirely), so there is nothing sensible to patch.
        this.channelsResource.reload();
      },
      error: (error: HttpErrorResponse) => {
        this.pendingChannel.set(null);
        this.actionError.set(apiErrorTranslationKey(error));
      },
    });
  }
}
