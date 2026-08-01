import { NgOptimizedImage } from '@angular/common';
import { Dialog } from '@angular/cdk/dialog';
import { ScrollingModule } from '@angular/cdk/scrolling';
import { Component, DestroyRef, computed, effect, inject, input, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { Router } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { Subscription, catchError, first, of, switchMap, take, timer } from 'rxjs';

import { ChannelService } from '../../core/channels/channel.service';
import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { EmoteSetStatus } from '../../core/emotes/emote-set-status.model';
import { pluralKey } from '../../core/i18n/plural';
import { VoteSessionSummary } from '../../core/voting/vote-session.model';
import { CreateVoteSessionDialog, CreateVoteSessionDialogData } from './create-vote-session-dialog';
import { LIVE_EVENT_TYPES, channelLiveUrl } from '../../core/live/live-event.model';
import { liveReload } from '../../core/live/live-reload';
import { EmoteUsageTotal } from '../../core/usage-stats/usage-stat.model';
import { UsageStatService } from '../../core/usage-stats/usage-stat.service';
import { DateRangePopover } from '../../shared/datetime/date-range-popover';
import { EmoteCardHeader } from '../../shared/emotes/emote-card-header';
import { EmoteUsageFilter } from '../../shared/emotes/emote-usage-filter';
import { SlotBudgetBar } from '../../shared/emotes/slot-budget-bar';
import { chunkIntoRows, computeGridColumns } from '../../shared/grid/grid-columns';
import { DeletableEmote, MassDeletePanel } from '../../shared/seven-tv/mass-delete-panel';
import { ListSelection } from '../../shared/selection/list-selection';
import { Button } from '../../shared/ui/button';
import { EmptyState } from '../../shared/ui/empty-state';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { SegmentedControl, SegmentedControlOption } from '../../shared/ui/segmented-control';

type SortDirection = 'asc' | 'desc';
type RangePreset = '0' | '7' | '30' | 'custom';

// Row height (px) fed to CdkVirtualScrollViewport — must match the fixed card height + row
// wrapper padding below, since CDK's fixed-size strategy assumes every virtualized row is the same
// height. Card h-28 (112) + row py-2 (16).
const ROW_HEIGHT_PX = 128;

// Joining a channel does not fill it with emotes right away: POST /join only writes the channel row
// and publishes JOIN to Redis, and the worker resolves the 7TV set a beat later. Since the overview
// navigates straight into the workspace, the user reliably landed inside that window and saw an
// empty grid that only a manual reload fixed. 30 seconds of polling covers a sync that normally
// takes one or two, with headroom for a slow 7TV.
const SYNC_POLL_INTERVAL_MS = 2000;
const SYNC_POLL_MAX_ATTEMPTS = 15;

// The worker flushes chat usage in 30-second batches, so pushes arrive in bursts rather than
// continuously. One second of debounce merges a burst (several channels' flushes land in the same
// tick) into a single refetch without making the update feel delayed.
const USAGE_RELOAD_DEBOUNCE_MS = 1000;

function toIsoDate(date: Date): string {
  return date.toISOString().slice(0, 10);
}

function daysAgo(days: number): Date {
  const date = new Date();
  date.setDate(date.getDate() - days);
  return date;
}

@Component({
  selector: 'app-usage-stats-page',
  imports: [
    Button,
    EmptyState,
    NoticeBanner,
    ScrollingModule,
    NgOptimizedImage,
    MassDeletePanel,
    EmoteCardHeader,
    SlotBudgetBar,
    SegmentedControl,
    DateRangePopover,
    TranslocoPipe,
  ],
  host: {
    '(window:resize)': 'updateColumns()',
  },
  templateUrl: './usage-stats-page.html',
})
export class UsageStatsPage {
  readonly channelName = input.required<string>();

  private readonly usageStatService = inject(UsageStatService);
  private readonly emoteAdminService = inject(EmoteAdminService);
  private readonly channelService = inject(ChannelService);
  private readonly dialog = inject(Dialog);
  private readonly router = inject(Router);

  // The route guard admits 7TV editors (canViewUsageStats), but creating a vote session is a
  // management action (ChannelManagementAuthorizationFilter on the endpoint) — the button only
  // shows where the click can succeed. Same pattern as VoteSessionListPage's create form.
  private readonly permissionsResource = rxResource({
    params: () => this.channelName(),
    stream: ({ params }) => this.channelService.getPermissions(params),
  });
  protected readonly canManage = computed(
    () => this.permissionsResource.value()?.canManage ?? false,
  );

  // A computed, not a field read in the constructor: channelName is a required input and reading it
  // during construction throws NG0950. computed() is lazy, so the first read happens inside
  // liveReload's toObservable effect, by which time the input is set.
  private readonly liveUrl = computed(() => channelLiveUrl(this.channelName()));

  protected readonly rowHeight = ROW_HEIGHT_PX;
  protected readonly columns = signal(computeGridColumns(window.innerWidth));

  protected readonly from = signal(toIsoDate(daysAgo(7)));
  protected readonly to = signal(toIsoDate(new Date()));
  protected readonly sortDirection = signal<SortDirection>('desc');

  // Which segment is lit; 'custom' opens the date-range popover instead of changing the dates.
  // Must match the initial from()/to() pair above.
  protected readonly rangePreset = signal<RangePreset>('7');
  protected readonly isCustomRangeOpen = signal(false);
  protected readonly presetOptions: SegmentedControlOption[] = [
    { value: '0', labelKey: 'usageStats.presetToday' },
    { value: '7', labelKey: 'usageStats.preset7Days' },
    { value: '30', labelKey: 'usageStats.preset30Days' },
    { value: 'custom', labelKey: 'usageStats.presetCustom' },
  ];

  protected readonly emotes = signal<EmoteUsageTotal[]>([]);
  protected readonly setStatus = signal<EmoteSetStatus | null>(null);
  protected readonly activeEmoteSetId = computed(() => this.setStatus()?.activeEmoteSetId || null);
  protected readonly isLoading = signal(false);
  protected readonly skeletonCells = Array.from({ length: 12 }, (_, i) => i);
  protected readonly isAwaitingSync = signal(false);
  protected readonly errorMessage = signal<string | null>(null);

  private readonly destroyRef = inject(DestroyRef);
  private syncPoll?: Subscription;

  // Prune, don't clear (S2-16): narrowing a filter keeps the still-visible part of the selection,
  // while anything filtered out is dropped so the delete path never holds an off-screen emote.
  protected readonly usageFilter = new EmoteUsageFilter<EmoteUsageTotal>(() =>
    this.selection.retainVisible(),
  );

  protected readonly filteredEmotes = computed(() => this.usageFilter.apply(this.emotes()));

  protected readonly sortedEmotes = computed(() => {
    const items = [...this.filteredEmotes()];
    items.sort((a, b) =>
      this.sortDirection() === 'desc'
        ? b.totalUseCount - a.totalUseCount
        : a.totalUseCount - b.totalUseCount,
    );
    return items;
  });

  protected readonly rows = computed(() => chunkIntoRows(this.sortedEmotes(), this.columns()));

  protected readonly emoteCountKey = computed(() => pluralKey(this.emotes().length, 'emoteCount'));

  protected readonly selection = new ListSelection(this.sortedEmotes, (emote) => emote.emoteId);

  // Resolved items, not selection.selectedKeys(): the delete engine needs sevenTvEmoteId and the
  // display name, which only the loaded row carries. Safe because every path that removes a row
  // from sortedEmotes() while keeping the page open (filter change, reload, finished delete)
  // clears the selection — so nothing selected can be missing here.
  protected readonly selectedForDelete = computed<DeletableEmote[]>(() =>
    this.selection.selectedItems().map((emote) => ({
      emoteId: emote.emoteId,
      sevenTvEmoteId: emote.sevenTvEmoteId,
      name: emote.emoteName,
    })),
  );

  constructor() {
    effect(() => {
      this.load(this.channelName(), this.from(), this.to());
    });
    this.destroyRef.onDestroy(() => this.syncPoll?.unsubscribe());

    // Live refresh after the worker's usage flush and after real emote-inventory changes
    // (`channel.synced` only fires when a sync actually changed something — add/remove on 7TV,
    // set swap, mass delete).
    // The reload is deliberately quiet: neither the selection nor the skeleton may move under a
    // user who did not ask for anything — this update arrives unrequested.
    liveReload(this.liveUrl, {
      accept: [LIVE_EVENT_TYPES.usageFlushed, LIVE_EVENT_TYPES.channelSynced],
      debounceMs: USAGE_RELOAD_DEBOUNCE_MS,
    }).subscribe((seen) => {
      this.loadTotals(this.channelName(), this.from(), this.to(), {
        preserveSelection: true,
        silent: true,
      });
      // Only a sync can have moved the active set id, its capacity or the occupied-slot count.
      if (seen.has(LIVE_EVENT_TYPES.channelSynced)) {
        this.refreshSetStatus();
      }
    });
  }

  protected updateColumns(): void {
    this.columns.set(computeGridColumns(window.innerWidth));
  }

  protected refresh(): void {
    this.load(this.channelName(), this.from(), this.to());
  }

  protected onPresetChange(value: string): void {
    this.rangePreset.set(value as RangePreset);
    this.isCustomRangeOpen.set(value === 'custom');
    if (value !== 'custom') {
      this.from.set(toIsoDate(daysAgo(Number(value))));
      this.to.set(toIsoDate(new Date()));
    }
  }

  // Clicking the already-selected "custom" segment reopens the popover after it was dismissed.
  protected onPresetReselected(value: string): void {
    if (value === 'custom') {
      this.isCustomRangeOpen.set(true);
    }
  }

  protected toggleSort(): void {
    this.sortDirection.update((direction) => (direction === 'desc' ? 'asc' : 'desc'));
  }

  protected onDeleted(deletedIds: string[]): void {
    this.emotes.update((items) => items.filter((item) => !deletedIds.includes(item.emoteId)));
    // Freed slots are shown right away rather than waiting for the channel.synced round trip the
    // bookkeeping call triggers — the emptied bar is the feedback the delete was run for. The
    // refetch that follows a moment later confirms or corrects it.
    this.setStatus.update((status) =>
      status
        ? { ...status, occupiedSlots: Math.max(status.occupiedSlots - deletedIds.length, 0) }
        : status,
    );
    this.selection.clear();
  }

  // Captures the selection at open time: loadTotals clears it on channel/date-range changes, so
  // the dialog holds its own copy of the ballot rather than a live view of the selection.
  protected openCreateVoteSession(): void {
    const emoteIds = [...this.selection.selectedKeys()];
    if (emoteIds.length === 0) {
      return;
    }

    const data: CreateVoteSessionDialogData = {
      channelName: this.channelName(),
      emoteIds,
      usageFromDate: this.from(),
    };
    this.dialog
      .open<VoteSessionSummary | undefined>(CreateVoteSessionDialog, {
        data,
        backdropClass: 'app-dialog-backdrop',
        panelClass: 'app-dialog-panel',
        ariaLabelledBy: 'create-vote-session-title',
      })
      .closed.subscribe((created) => {
        if (created) {
          this.selection.clear();
          this.router.navigate(['/channels', this.channelName(), 'vote-sessions', created.id]);
        }
      });
  }

  // The delete run finished on 7TV, but the backend could not confirm it — refetch instead of
  // filtering locally, so the list never claims a state the server does not share.
  protected onReloadRequested(): void {
    this.selection.clear();
    this.refresh();
  }

  // Quiet counterpart to the set-status fetch in load(): no sync-poll, and a failed refetch keeps
  // the current value — this runs unrequested, so it must never take the mass-delete panel away
  // over a transient error.
  private refreshSetStatus(): void {
    this.emoteAdminService.getSetStatus(this.channelName()).subscribe({
      next: (status) => this.setStatus.set(status),
      error: () => undefined,
    });
  }

  private load(channelName: string, from: string, to: string): void {
    // A poll from a previous channel or date range must not survive into this one.
    this.syncPoll?.unsubscribe();
    this.isAwaitingSync.set(false);
    this.isLoading.set(true);
    this.errorMessage.set(null);

    this.emoteAdminService.getSetStatus(channelName).subscribe({
      next: (status) => {
        this.setStatus.set(status);
        // An empty id means SevenTvSyncService has not completed a run for this channel yet. It is
        // the only thing that tells "sync still pending" apart from "channel genuinely has no
        // emotes" — an empty totals response looks identical in both cases.
        if (!status.activeEmoteSetId) {
          this.awaitSync(channelName, from, to);
        }
      },
      error: () => this.setStatus.set(null),
    });

    this.loadTotals(channelName, from, to);
  }

  // Waits for the worker's 7TV sync to fill in the set id, then loads the totals once more.
  // Deliberately bounded: a channel with no 7TV emote set at all never gets an id, so this has to
  // give up eventually — at which point the ordinary "no active emotes" state is the honest answer.
  private awaitSync(channelName: string, from: string, to: string): void {
    this.isAwaitingSync.set(true);
    this.syncPoll = timer(SYNC_POLL_INTERVAL_MS, SYNC_POLL_INTERVAL_MS)
      .pipe(
        // catchError sits on the inner request, not on the outer pipe: out here it would replace the
        // whole polling stream on the first hiccup and end the wait. Inside, one failed tick just
        // counts as "still empty" and the next tick tries again.
        switchMap(() =>
          this.emoteAdminService.getSetStatus(channelName).pipe(catchError(() => of(null))),
        ),
        take(SYNC_POLL_MAX_ATTEMPTS),
        // Completes on the first status carrying a set id; if the attempts run out first, the
        // default null arrives instead, so the subscriber always runs exactly once and never errors.
        first((status) => !!status?.activeEmoteSetId, null),
      )
      .subscribe((status) => {
        this.isAwaitingSync.set(false);
        if (status?.activeEmoteSetId) {
          this.setStatus.set(status);
          this.loadTotals(channelName, from, to);
        }
      });
  }

  /**
   * `preserveSelection` and `silent` are what separates a user-triggered load from a pushed one:
   * a live update must not throw away a half-built delete selection, and must not flash the
   * skeleton over numbers the user is currently reading. Both default to the loud behaviour, so
   * every existing caller (initial load, refresh button, sync poll) is unchanged.
   */
  private loadTotals(
    channelName: string,
    from: string,
    to: string,
    options: { preserveSelection?: boolean; silent?: boolean } = {},
  ): void {
    this.usageStatService.getTotals(channelName, from, to).subscribe({
      next: (emotes) => {
        this.emotes.set(emotes);
        // Kept even though a keyed selection survives a plain refetch: load() also runs on a
        // channel or date-range change, where the existing selection was made against different
        // numbers (an emote with "0x in 7 days" may be heavily used over 30 days). Carrying it
        // over would be its own deliberate feature, not a by-product of the keying.
        if (!options.preserveSelection) {
          this.selection.clear();
        }
        if (!options.silent) {
          this.isLoading.set(false);
        }
      },
      // 401 is not handled here — apiAuthInterceptor resets the session and redirects for every
      // /api/ call in the app.
      error: () => {
        this.errorMessage.set('usageStats.errors.loadFailed');
        if (!options.silent) {
          this.isLoading.set(false);
        }
      },
    });
  }
}
