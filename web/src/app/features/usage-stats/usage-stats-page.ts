import { NgOptimizedImage } from '@angular/common';
import { ScrollingModule } from '@angular/cdk/scrolling';
import { Component, DestroyRef, computed, effect, inject, input, signal } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { Subscription, catchError, first, map, of, switchMap, take, timer } from 'rxjs';

import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { pluralKey } from '../../core/i18n/plural';
import { EmoteUsageTotal } from '../../core/usage-stats/usage-stat.model';
import { UsageStatService } from '../../core/usage-stats/usage-stat.service';
import { EmoteCardHeader } from '../../shared/emotes/emote-card-header';
import { EmoteUsageFilter } from '../../shared/emotes/emote-usage-filter';
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
// wrapper padding below, since CDK's fixed-size strategy assumes every virtualized row is the same height.
const ROW_HEIGHT_PX = 112;

// Joining a channel does not fill it with emotes right away: POST /join only writes the channel row
// and publishes JOIN to Redis, and the worker resolves the 7TV set a beat later. Since the overview
// navigates straight into the workspace, the user reliably landed inside that window and saw an
// empty grid that only a manual reload fixed. 30 seconds of polling covers a sync that normally
// takes one or two, with headroom for a slow 7TV.
const SYNC_POLL_INTERVAL_MS = 2000;
const SYNC_POLL_MAX_ATTEMPTS = 15;

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
    SegmentedControl,
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

  protected readonly rowHeight = ROW_HEIGHT_PX;
  protected readonly columns = signal(computeGridColumns(window.innerWidth));

  protected readonly from = signal(toIsoDate(daysAgo(7)));
  protected readonly to = signal(toIsoDate(new Date()));
  protected readonly sortDirection = signal<SortDirection>('desc');

  // Which segment is lit; 'custom' exposes the two date inputs instead of changing the dates.
  // Must match the initial from()/to() pair above.
  protected readonly rangePreset = signal<RangePreset>('7');
  protected readonly presetOptions: SegmentedControlOption[] = [
    { value: '0', labelKey: 'usageStats.presetToday' },
    { value: '7', labelKey: 'usageStats.preset7Days' },
    { value: '30', labelKey: 'usageStats.preset30Days' },
    { value: 'custom', labelKey: 'usageStats.presetCustom' },
  ];

  protected readonly emotes = signal<EmoteUsageTotal[]>([]);
  protected readonly activeEmoteSetId = signal<string | null>(null);
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
  }

  protected updateColumns(): void {
    this.columns.set(computeGridColumns(window.innerWidth));
  }

  protected refresh(): void {
    this.load(this.channelName(), this.from(), this.to());
  }

  protected onPresetChange(value: string): void {
    this.rangePreset.set(value as RangePreset);
    if (value !== 'custom') {
      this.from.set(toIsoDate(daysAgo(Number(value))));
      this.to.set(toIsoDate(new Date()));
    }
  }

  protected toggleSort(): void {
    this.sortDirection.update((direction) => (direction === 'desc' ? 'asc' : 'desc'));
  }

  protected onDeleted(deletedIds: string[]): void {
    this.emotes.update((items) => items.filter((item) => !deletedIds.includes(item.emoteId)));
    this.selection.clear();
  }

  // The delete run finished on 7TV, but the backend could not confirm it — refetch instead of
  // filtering locally, so the list never claims a state the server does not share.
  protected onReloadRequested(): void {
    this.selection.clear();
    this.refresh();
  }

  private load(channelName: string, from: string, to: string): void {
    // A poll from a previous channel or date range must not survive into this one.
    this.syncPoll?.unsubscribe();
    this.isAwaitingSync.set(false);
    this.isLoading.set(true);
    this.errorMessage.set(null);

    this.emoteAdminService.getActiveEmoteSetId(channelName).subscribe({
      next: (result) => {
        this.activeEmoteSetId.set(result.activeEmoteSetId);
        // An empty id means SevenTvSyncService has not completed a run for this channel yet. It is
        // the only thing that tells "sync still pending" apart from "channel genuinely has no
        // emotes" — an empty totals response looks identical in both cases.
        if (!result.activeEmoteSetId) {
          this.awaitSync(channelName, from, to);
        }
      },
      error: () => this.activeEmoteSetId.set(null),
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
          this.emoteAdminService
            .getActiveEmoteSetId(channelName)
            .pipe(catchError(() => of({ activeEmoteSetId: '' }))),
        ),
        map((result) => result.activeEmoteSetId),
        take(SYNC_POLL_MAX_ATTEMPTS),
        // Completes on the first non-empty id; if the attempts run out first, the default '' arrives
        // instead, so the subscriber always runs exactly once and never errors.
        first((setId) => setId.length > 0, ''),
      )
      .subscribe((setId) => {
        this.isAwaitingSync.set(false);
        if (setId) {
          this.activeEmoteSetId.set(setId);
          this.loadTotals(channelName, from, to);
        }
      });
  }

  private loadTotals(channelName: string, from: string, to: string): void {
    this.usageStatService.getTotals(channelName, from, to).subscribe({
      next: (emotes) => {
        this.emotes.set(emotes);
        // Kept even though a keyed selection survives a plain refetch: load() also runs on a
        // channel or date-range change, where the existing selection was made against different
        // numbers (an emote with "0x in 7 days" may be heavily used over 30 days). Carrying it
        // over would be its own deliberate feature, not a by-product of the keying.
        this.selection.clear();
        this.isLoading.set(false);
      },
      // 401 is not handled here — apiAuthInterceptor resets the session and redirects for every
      // /api/ call in the app.
      error: () => {
        this.errorMessage.set('usageStats.errors.loadFailed');
        this.isLoading.set(false);
      },
    });
  }
}
