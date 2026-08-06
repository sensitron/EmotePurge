import { NgOptimizedImage } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Dialog } from '@angular/cdk/dialog';
import { CdkVirtualScrollViewport, ScrollingModule } from '@angular/cdk/scrolling';
import {
  Component,
  DestroyRef,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { Router } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { Subscription, catchError, first, of, switchMap, take, timer } from 'rxjs';

import { ChannelService } from '../../core/channels/channel.service';
import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { EmoteSetStatus } from '../../core/emotes/emote-set-status.model';
import { apiErrorTranslationKey } from '../../core/i18n/api-error';
import { LanguageService } from '../../core/i18n/language.service';
import { toLocale } from '../../core/i18n/locale';
import { pluralKey } from '../../core/i18n/plural';
import { SevenTvDeleteService } from '../../core/seven-tv/seven-tv-delete.service';
import { SevenTvRestoreService } from '../../core/seven-tv/seven-tv-restore.service';
import { VoteSessionSummary } from '../../core/voting/vote-session.model';
import { CreateVoteSessionDialog, CreateVoteSessionDialogData } from './create-vote-session-dialog';
import { LIVE_EVENT_TYPES, channelLiveUrl } from '../../core/live/live-event.model';
import { liveReload } from '../../core/live/live-reload';
import { EmoteUsageTotal } from '../../core/usage-stats/usage-stat.model';
import { UsageStatService } from '../../core/usage-stats/usage-stat.service';
import {
  DateRangeMenu,
  DateRangePreset,
  allTimeStart,
  toIsoDate,
} from '../../shared/datetime/date-range-menu';
import {
  EmoteDrilldownData,
  EmoteDrilldownDialog,
} from '../../shared/emotes/emote-drilldown-dialog';
import {
  UsageTrend,
  daysInSet,
  isUnderObservation,
  usageTrend,
} from '../../shared/emotes/emote-context';
import { EmoteUsageFilter } from '../../shared/emotes/emote-usage-filter';
import {
  UsageBandKey,
  groupIntoUsageBands,
  topFifthShare,
  usageBandOf,
  usageBandThresholds,
  usageDistribution,
  usageFillPercent,
} from '../../shared/emotes/usage-bands';
import { SlotBudgetBar } from '../../shared/emotes/slot-budget-bar';
import { CSV_MIME } from '../../shared/export/csv';
import { ExportChoice, ExportDialog, ExportDialogData } from '../../shared/export/export-dialog';
import { JSON_MIME } from '../../shared/export/export-envelope';
import { downloadFile } from '../../shared/export/file-download';
import {
  UsageExportInput,
  usageCsv,
  usageExportFilename,
  usageJson,
} from '../../shared/export/usage-export';
import {
  ATLAS_CELL_PX,
  ATLAS_ROW_PX,
  ATLAS_STICKY_TOP_PX,
  SIDECAR_GAP_PX,
  atlasColumns,
  atlasRowOfIndex,
  moveInAtlas,
  packAtlasRows,
} from '../../shared/grid/atlas-grid';
import { DeletableEmote, MassDeletePanel } from '../../shared/seven-tv/mass-delete-panel';
import { RestorePanel } from '../../shared/seven-tv/restore-panel';
import { ListSelection } from '../../shared/selection/list-selection';
import { Button } from '../../shared/ui/button';
import { EmptyState } from '../../shared/ui/empty-state';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { SegmentedControl, SegmentedControlOption } from '../../shared/ui/segmented-control';

type SortDirection = 'asc' | 'desc';
type SortKey = 'usage' | 'lastUsed';

// Sorting a never-used emote needs a position, not a crash. It is the deadest thing in the list, so
// it sorts as older than any real date: descending (most recent first) puts them at the very end,
// ascending puts them at the front, and either way they stay together instead of scattering.
const NEVER_USED_SORT_VALUE = Number.NEGATIVE_INFINITY;

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

// Buckets in the distribution strip. Around a hundred is what fits across the content width at one
// readable bar plus gutter — enough to show where the curve's knee sits, few enough that each bar
// stays a bar rather than a hairline.
const DISTRIBUTION_BUCKETS = 96;

function sortableLastUsed(lastUsedDate: string | null): number {
  if (!lastUsedDate) {
    return NEVER_USED_SORT_VALUE;
  }

  const parsed = Date.parse(`${lastUsedDate}T00:00:00Z`);
  return Number.isNaN(parsed) ? NEVER_USED_SORT_VALUE : parsed;
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
    RestorePanel,
    SlotBudgetBar,
    DateRangeMenu,
    SegmentedControl,
    TranslocoPipe,
  ],
  templateUrl: './usage-stats-page.html',
})
export class UsageStatsPage {
  readonly channelName = input.required<string>();

  private readonly usageStatService = inject(UsageStatService);
  private readonly emoteAdminService = inject(EmoteAdminService);
  private readonly channelService = inject(ChannelService);
  private readonly languageService = inject(LanguageService);
  private readonly deleteService = inject(SevenTvDeleteService);
  private readonly restoreService = inject(SevenTvRestoreService);
  private readonly dialog = inject(Dialog);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  // The sheet element is what the column count is measured against — see atlasColumns() for why the
  // window's width was the wrong ruler. Unconditionally rendered (it wraps the loading, empty and
  // atlas states alike) so `required` can never miss it.
  private readonly sheetRef = viewChild.required<ElementRef<HTMLElement>>('sheet');
  private readonly stickyBarRef = viewChild.required<ElementRef<HTMLElement>>('stickyBar');
  private readonly viewport = viewChild(CdkVirtualScrollViewport);

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

  protected readonly cellPx = ATLAS_CELL_PX;
  protected readonly rowHeight = ATLAS_ROW_PX;
  protected readonly sheetWidth = signal(0);
  protected readonly columns = computed(() => atlasColumns(this.sheetWidth()));

  /** Where the sidecar pins, measured against the toolbar rather than hard-coded — the filter row
   *  wraps into one, two or three rows depending on the width, and a fixed offset would leave a gap
   *  at one end and clip the panel's first line at the other. */
  protected readonly sidecarTop = signal(ATLAS_STICKY_TOP_PX + SIDECAR_GAP_PX);

  // trackedSince is not known until the set-status response lands, so the first request uses the
  // widest span the endpoint accepts — identical rows for any channel younger than that. Once the
  // response arrives, the constructor effect below pulls from() forward to the tracking start, so
  // "all time" never keeps claiming a year of data on a channel tracked for days.
  protected readonly from = signal(allTimeStart(null));
  protected readonly to = signal(toIsoDate(new Date()));
  protected readonly sortKey = signal<SortKey>('usage');
  protected readonly sortDirection = signal<SortDirection>('desc');

  protected readonly sortKeyOptions: SegmentedControlOption[] = [
    { value: 'usage', labelKey: 'usageStats.sortByUsage' },
    { value: 'lastUsed', labelKey: 'usageStats.sortByLastUsed' },
  ];

  protected readonly sortDirectionOptions: SegmentedControlOption[] = [
    { value: 'desc', labelKey: 'usageStats.sortDescending' },
    { value: 'asc', labelKey: 'usageStats.sortAscending' },
  ];

  // "All time" is the default because this page exists to answer "has this emote ever been used".
  // A rolling 7-day window answered a different question and got it wrong twice over: it made a
  // long-serving emote that had a quiet week look like a deletion candidate, and on a freshly
  // joined channel it reached back before tracking started, so the very first thing a new user saw
  // was the incomplete-data warning banner. Owned here rather than inside the menu so the page can
  // name the range it is showing without reaching into a child; must match from()/to() above.
  protected readonly rangePreset = signal<DateRangePreset>('all');

  protected readonly emotes = signal<EmoteUsageTotal[]>([]);
  protected readonly setStatus = signal<EmoteSetStatus | null>(null);
  protected readonly activeEmoteSetId = computed(() => this.setStatus()?.activeEmoteSetId || null);
  protected readonly trackedSince = computed(() => this.setStatus()?.trackedSince ?? null);

  /** Date-only form, which is what the range menu and the vote-session ballot both speak. */
  protected readonly trackedSinceDate = computed(() => this.trackedSince()?.slice(0, 10) ?? null);

  // The selected range reaches back further than we have been counting, so its leading part is
  // silently empty. Saying so is the difference between "this emote is dead" and "we weren't here".
  protected readonly rangeStartsBeforeTracking = computed(() => {
    // "All time" means "everything we have", not a range someone chose that happens to start too
    // early — warning about its own definition would fire on every default page load.
    if (this.rangePreset() === 'all') {
      return false;
    }

    const trackedSince = this.trackedSince();
    if (!trackedSince) {
      return false;
    }

    const trackingStart = Date.parse(trackedSince);
    const rangeStart = Date.parse(`${this.from()}T00:00:00Z`);
    return !Number.isNaN(trackingStart) && !Number.isNaN(rangeStart) && rangeStart < trackingStart;
  });
  protected readonly isLoading = signal(false);
  protected readonly skeletonCells = Array.from({ length: 48 }, (_, i) => i);
  protected readonly isAwaitingSync = signal(false);
  protected readonly errorMessage = signal<string | null>(null);

  private syncPoll?: Subscription;

  // Prune, don't clear (S2-16): narrowing a filter keeps the still-visible part of the selection,
  // while anything filtered out is dropped so the delete path never holds an off-screen emote.
  protected readonly usageFilter = new EmoteUsageFilter<EmoteUsageTotal>(() =>
    this.selection.retainVisible(),
  );

  protected readonly filteredEmotes = computed(() => this.usageFilter.apply(this.emotes()));

  protected readonly sortedEmotes = computed(() => {
    const key = this.sortKey();
    const factor = this.sortDirection() === 'desc' ? -1 : 1;
    const value = (emote: EmoteUsageTotal) =>
      key === 'usage' ? emote.totalUseCount : sortableLastUsed(emote.lastUsedDate);

    const items = [...this.filteredEmotes()];
    // Name as the tiebreaker: "last used" collapses to a handful of distinct days, and without it
    // equal-day emotes would reshuffle on every refetch.
    items.sort(
      (a, b) =>
        factor * (value(a) - value(b)) ||
        a.emoteName.localeCompare(b.emoteName, undefined, { sensitivity: 'base' }),
    );
    return items;
  });

  // Derived from the WHOLE set, not from the filtered view: the weight classes are a property of
  // the channel, and they must not move under the user because they typed three letters into the
  // name filter. Otherwise an emote would change band while nothing about it changed.
  private readonly bandThresholds = computed(() =>
    usageBandThresholds(this.emotes().map((emote) => emote.totalUseCount)),
  );

  protected readonly bands = computed(() =>
    groupIntoUsageBands(this.sortedEmotes(), (emote) => emote.totalUseCount, this.bandThresholds()),
  );

  /**
   * The atlas's true display order: bands first, the chosen sort within each band. Everything
   * position-dependent — shift-click ranges, keyboard navigation, the export — reads this rather
   * than sortedEmotes(), or a shift-click would select a range the user never saw as contiguous.
   */
  protected readonly atlasOrder = computed(() => this.bands().flatMap((band) => band.items));

  protected readonly rows = computed(() => packAtlasRows(this.bands(), this.columns()).rows);

  /** Fill-bar width per emote id — precomputed per band peak so the template stays a lookup. */
  protected readonly fillPercents = computed(() => {
    const map = new Map<string, number>();
    for (const band of this.bands()) {
      for (const emote of band.items) {
        map.set(emote.emoteId, usageFillPercent(emote.totalUseCount, band.peak));
      }
    }
    return map;
  });

  /** Ranked usage curve of the whole set — the orientation device above the sheet. */
  protected readonly distribution = computed(() =>
    usageDistribution(
      this.emotes().map((emote) => emote.totalUseCount),
      DISTRIBUTION_BUCKETS,
    ),
  );

  /**
   * How many leading buckets of the strip belong to the heavy band, so the curve's head is drawn in
   * the guide colour and the band headers below are recognisable in it. Derived from the share of
   * heavy emotes rather than counted per bucket — the buckets are equal slices of the ranking, so
   * the two agree to within one bar and the cheap form is the one worth having.
   */
  protected readonly heavyBuckets = computed(() => {
    const all = this.emotes();
    if (all.length === 0) {
      return 0;
    }
    const thresholds = this.bandThresholds();
    const heavy = all.filter(
      (emote) => usageBandOf(emote.totalUseCount, thresholds) === 'heavy',
    ).length;
    // Against the strip's actual bar count, not the requested one — a set smaller than the bucket
    // budget gets one bar per emote, and the accent prefix has to follow that.
    return Math.round((heavy / all.length) * this.distribution().length);
  });

  protected readonly concentration = computed(() =>
    topFifthShare(this.emotes().map((emote) => emote.totalUseCount)),
  );

  protected readonly deadCount = computed(
    () => this.emotes().filter((emote) => emote.totalUseCount === 0).length,
  );

  private readonly totalUses = computed(() =>
    this.emotes().reduce((sum, emote) => sum + emote.totalUseCount, 0),
  );

  /** Position in the set's usage ranking — the inspector's "#003", stable across sort changes. */
  private readonly usageRank = computed(() => {
    const ranks = new Map<string, number>();
    [...this.emotes()]
      .sort((a, b) => b.totalUseCount - a.totalUseCount)
      .forEach((emote, index) => ranks.set(emote.emoteId, index + 1));
    return ranks;
  });

  // Plural follows the number that is actually spelled first ("1 von 409 Emote", not "Emotes").
  protected readonly emoteCountKey = computed(() =>
    pluralKey(this.atlasOrder().length, 'emoteCount'),
  );

  protected readonly selection = new ListSelection(this.atlasOrder, (emote) => emote.emoteId);

  /**
   * The cell the inspector is describing, held by id rather than by object: a refetch hands out
   * fresh instances for the same rows, and an object reference would leave the inspector pinned to
   * a row that no longer exists. Falls back to the busiest emote so the line is never empty —
   * before the first hover, the top of the set is the honest thing to be looking at.
   */
  private readonly inspectedId = signal<string | null>(null);
  protected readonly inspected = computed(() => {
    const order = this.atlasOrder();
    const id = this.inspectedId();
    return (id ? order.find((emote) => emote.emoteId === id) : undefined) ?? order[0] ?? null;
  });

  protected readonly inspectedRank = computed(() => {
    const emote = this.inspected();
    return emote ? (this.usageRank().get(emote.emoteId) ?? null) : null;
  });

  protected readonly inspectedBand = computed(() => {
    const emote = this.inspected();
    return emote ? usageBandOf(emote.totalUseCount, this.bandThresholds()) : 'dead';
  });

  protected readonly inspectedShare = computed(() => {
    const emote = this.inspected();
    const total = this.totalUses();
    return emote && total > 0 ? emote.totalUseCount / total : null;
  });

  /** The single tab stop in the grid (WAI-ARIA grid pattern) — arrow keys move it. */
  protected readonly activeIndex = signal(0);

  // Resolved items, not selection.selectedKeys(): the delete engine needs sevenTvEmoteId and the
  // display name, which only the loaded row carries. Safe because every path that removes a row
  // from the atlas while keeping the page open (filter change, reload, finished delete) clears the
  // selection — so nothing selected can be missing here.
  protected readonly selectedForDelete = computed<DeletableEmote[]>(() =>
    this.selection.selectedItems().map((emote) => ({
      emoteId: emote.emoteId,
      sevenTvEmoteId: emote.sevenTvEmoteId,
      name: emote.emoteName,
    })),
  );

  /**
   * The action bar is bound to the selection, but it must not disappear the moment a finished
   * delete clears it: the run summary carries the protocol download, and that file is the only
   * durable record of what was removed. So a live or settled run keeps the dock mounted.
   */
  protected readonly dockVisible = computed(
    () =>
      this.selection.selectedKeys().length > 0 ||
      this.deleteService.isRunning() ||
      this.deleteService.queue().length > 0 ||
      this.restoreService.isRunning() ||
      this.restoreService.queue().length > 0,
  );

  /** Occupied slots after the pending selection would be deleted — the dock's one number. */
  protected readonly projectedSlots = computed(() => {
    const status = this.setStatus();
    if (!status || status.capacity === null) {
      return null;
    }
    return {
      projected: Math.max(status.occupiedSlots - this.selection.selectedKeys().length, 0),
      capacity: status.capacity,
    };
  });

  constructor() {
    effect(() => {
      this.load(this.channelName(), this.from(), this.to());
    });

    // Resolves "all time" against the tracking start as soon as it is known — the initial from()
    // is only a placeholder (see its declaration). Every consumer of from() (grid request,
    // drilldown, export, vote-session prefill) inherits the corrected value, at the cost of one
    // repeated totals request per page open. Re-selecting "all" in the menu writes the same date
    // again, which a signal treats as no change — so this cannot loop.
    effect(() => {
      const trackedSinceDate = this.trackedSinceDate();
      if (this.rangePreset() === 'all' && trackedSinceDate) {
        this.from.set(allTimeStart(trackedSinceDate));
      }
    });

    // The column count follows the element that actually holds the cells. The incumbent grid read
    // window.innerWidth while the shell caps content at 1024 px, which is how a 2560 px monitor
    // ended up with eight 113 px cards in a 992 px container.
    effect((onCleanup) => {
      const element = this.sheetRef().nativeElement;
      this.sheetWidth.set(element.clientWidth);
      const observer = new ResizeObserver((entries) => {
        this.sheetWidth.set(entries[0].contentRect.width);
      });
      observer.observe(element);
      onCleanup(() => observer.disconnect());
    });

    effect((onCleanup) => {
      const element = this.stickyBarRef().nativeElement;
      const measure = () =>
        this.sidecarTop.set(ATLAS_STICKY_TOP_PX + element.offsetHeight + SIDECAR_GAP_PX);
      measure();
      const observer = new ResizeObserver(measure);
      observer.observe(element);
      onCleanup(() => observer.disconnect());
    });

    // A shorter list must not leave the tab stop pointing past its end — the next arrow key would
    // then find no row to move from and the grid would be unreachable by keyboard.
    effect(() => {
      if (this.activeIndex() >= this.atlasOrder().length) {
        this.activeIndex.set(0);
      }
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

  protected refresh(): void {
    this.load(this.channelName(), this.from(), this.to());
  }

  /** Signature takes `string` because SegmentedControl is untyped by design — one control for every
   *  small mutually-exclusive set, and a generic there would have to be threaded through every use. */
  protected setSortKey(key: string): void {
    if (this.sortKey() === key) {
      return;
    }

    this.sortKey.set(key as SortKey);
    // Every key change starts at "most first" again. Carrying an ascending order over to a
    // different column silently answers a question nobody asked twice.
    this.sortDirection.set('desc');
    // A different sort key reorders the whole grid, and the selection carries shift-range anchors
    // into that new order — keeping it would let the next shift-click sweep up rows the user never
    // saw next to each other. Flipping the direction alone reverses a list they are still looking
    // at, so that keeps the selection (see setSortDirection, which does not clear).
    this.selection.clear();
  }

  /** Keeps the selection on purpose: reversing a list the user is still looking at does not move
   *  anything out from under a shift-range anchor the way a different column does. */
  protected setSortDirection(direction: string): void {
    this.sortDirection.set(direction as SortDirection);
  }

  protected inspect(emote: EmoteUsageTotal): void {
    this.inspectedId.set(emote.emoteId);
  }

  /** Pointer or focus landing on a cell makes it both the inspected row and the grid's tab stop. */
  protected onCellFocus(emote: EmoteUsageTotal, index: number): void {
    this.inspectedId.set(emote.emoteId);
    this.activeIndex.set(index);
  }

  /**
   * A click selects, and it also pins the inspector to the clicked cell.
   *
   * The pinning is what makes the inspector usable without a pointer at all: on a touch screen
   * there is no hover to drive it, so without this the line would keep describing the busiest emote
   * no matter what the user tapped. Safari also does not focus a button on click, so relying on the
   * focus handler alone would leave the same gap on a desktop browser.
   */
  protected onCellClick(emote: EmoteUsageTotal, index: number, event: MouseEvent): void {
    this.inspectedId.set(emote.emoteId);
    this.activeIndex.set(index);
    this.selection.onRowClick(emote, event);
  }

  protected onAtlasKeydown(event: KeyboardEvent): void {
    // Space marks, Enter opens the history. They used to do the same thing, because both fire a
    // native click on a <button> — which left the keyboard with no way at all to reach the per-cell
    // trigger without giving it a second tab stop in a grid that deliberately has one.
    if (event.key === 'Enter') {
      const emote = this.atlasOrder()[this.activeIndex()];
      if (emote) {
        // Before the browser turns this keydown into a click on the cell, which would select it.
        event.preventDefault();
        this.openDrilldown(emote);
      }
      return;
    }

    const next = moveInAtlas(this.rows(), this.activeIndex(), event.key);
    if (next === null) {
      return;
    }

    // Only now: an unhandled key (typing into nothing, Tab out of the grid) must keep its default.
    event.preventDefault();
    this.activeIndex.set(next);
    this.focusCell(next);
  }

  protected selectBand(key: UsageBandKey): void {
    const band = this.bands().find((candidate) => candidate.key === key);
    if (band) {
      this.selection.selectMany(band.items);
    }
  }

  protected fillPercent(emote: EmoteUsageTotal): number {
    return this.fillPercents().get(emote.emoteId) ?? 0;
  }

  /** Compact figure printed onto the sprite — "9,4k" fits a 64 px cell, "9412" does not. */
  protected shortCount(count: number): string {
    const locale = toLocale(this.languageService.lang());
    return count >= 1000
      ? `${(count / 1000).toLocaleString(locale, { maximumFractionDigits: 1 })}k`
      : count.toLocaleString(locale);
  }

  protected formatCount(count: number): string {
    return count.toLocaleString(toLocale(this.languageService.lang()));
  }

  protected formatPercent(share: number): string {
    return share.toLocaleString(toLocale(this.languageService.lang()), {
      style: 'percent',
      maximumFractionDigits: share < 0.1 ? 1 : 0,
    });
  }

  protected trendFor(emote: EmoteUsageTotal): UsageTrend {
    const trackedSince = this.trackedSince();
    if (!trackedSince) {
      return 'unknown';
    }

    return usageTrend({
      totalUseCount: emote.totalUseCount,
      previousWindowUseCount: emote.previousWindowUseCount,
      firstSeenAt: emote.firstSeenAt,
      windowStart: this.from(),
      windowEnd: this.to(),
      trackedSince,
    });
  }

  protected isUnderObservation(emote: EmoteUsageTotal): boolean {
    return isUnderObservation(emote.firstSeenAt, new Date());
  }

  /** Whole days the emote has been in the set; floored at 0 so a clock skew cannot read as "-1". */
  protected observationDays(emote: EmoteUsageTotal): number {
    return Math.max(daysInSet(emote.firstSeenAt, new Date()) ?? 0, 0);
  }

  // Day zero gets its own wording: "seit 0 Tagen" is not a sentence anyone writes.
  protected observationLabelKey(emote: EmoteUsageTotal): string {
    const days = this.observationDays(emote);
    return days === 0
      ? 'usageStats.observationToday'
      : pluralKey(days, 'usageStats.observationAge');
  }

  protected formatDate(iso: string | null): string {
    if (!iso) {
      return '—';
    }

    // LOCALE_ID is bootstrap-time static and cannot follow a runtime language switch, so dates go
    // through toLocale() — same as the admin pages.
    return new Date(iso).toLocaleDateString(toLocale(this.languageService.lang()), {
      dateStyle: 'medium',
    });
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
      // The dialog turns this into the session's "count usage from" prefill. On the "all time"
      // preset from() already equals the tracking start (the constructor effect keeps it there),
      // so it is a date a human would recognise on every path.
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

  // Opened from the inspector, not from the cell: the cell click belongs to the selection, and a
  // 64 px sprite has no room for a second control that would not be a mis-click waiting to happen.
  // The dialog loads the series on its own through the service cache, so nothing is fetched until
  // the user explicitly asks for the history.
  protected openDrilldown(emote: EmoteUsageTotal): void {
    const data: EmoteDrilldownData = {
      channelName: this.channelName(),
      from: this.from(),
      to: this.to(),
      emoteId: emote.emoteId,
      emoteName: emote.emoteName,
      imageUrl: emote.imageUrl,
      firstSeenAt: emote.firstSeenAt,
      previousWindowUseCount: emote.previousWindowUseCount,
      trackedSince: this.trackedSince(),
    };
    this.dialog.open<void>(EmoteDrilldownDialog, {
      data,
      backdropClass: 'app-dialog-backdrop',
      panelClass: 'app-dialog-panel',
      ariaLabelledBy: 'emote-drilldown-title',
    });
  }

  // Exports the *visible* list (filtered + sorted, in atlas order) by default, or — chosen in the
  // dialog — the current selection: the same rows that drive mass-delete and vote-session creation.
  // Client-side serialization on purpose — the read model is already loaded, and a download must
  // never see more than the page does (A12).
  protected openExport(): void {
    const data: ExportDialogData = {
      rowCount: this.atlasOrder().length,
      filtered: this.usageFilter.isAnyActive(),
      selectionCount: this.selection.selectedKeys().length,
      // Whoever can open this page sees every usage figure — nothing to explain away here.
      noticeKeys: [],
    };
    this.dialog
      .open<ExportChoice | undefined>(ExportDialog, {
        data,
        backdropClass: 'app-dialog-backdrop',
        panelClass: 'app-dialog-panel',
        ariaLabelledBy: 'export-dialog-title',
      })
      .closed.subscribe((choice) => {
        if (!choice) {
          return;
        }
        // Built after the dialog closes, from the chosen scope. selectedItems() is safe here for
        // the same reason as selectedForDelete: everything that removes rows from the atlas also
        // clears or prunes the selection.
        const input: UsageExportInput = {
          channelName: this.channelName(),
          from: this.from(),
          to: this.to(),
          rows: choice.scope === 'selection' ? this.selection.selectedItems() : this.atlasOrder(),
          scope: choice.scope,
          filtered: this.usageFilter.isAnyActive(),
          trendFor: (row) => this.trendFor(row),
        };
        if (choice.format === 'csv') {
          downloadFile(usageExportFilename(input, 'csv'), usageCsv(input), CSV_MIME);
        } else {
          downloadFile(usageExportFilename(input, 'json'), usageJson(input), JSON_MIME);
        }
      });
  }

  // The delete run finished on 7TV, but the backend could not confirm it — refetch instead of
  // filtering locally, so the list never claims a state the server does not share.
  protected onReloadRequested(): void {
    this.selection.clear();
    this.refresh();
  }

  /**
   * Moves DOM focus onto a cell that the virtual scroller may not have rendered yet.
   *
   * The straightforward `querySelector().focus()` covers every move inside the mounted range, which
   * is the common case; only a jump past the buffer (Home, End, an arrow at the edge) needs the
   * viewport scrolled first and the focus deferred until CDK has mounted the new range.
   */
  private focusCell(index: number): void {
    const find = () =>
      this.sheetRef().nativeElement.querySelector<HTMLElement>(`[data-atlas-index="${index}"]`);

    const rendered = find();
    if (rendered) {
      rendered.focus();
      return;
    }

    const rowIndex = atlasRowOfIndex(this.rows(), index);
    if (rowIndex < 0) {
      return;
    }
    this.viewport()?.scrollToIndex(rowIndex);
    requestAnimationFrame(() => find()?.focus());
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
    // A poll from a previous channel or date range must not survive into this one — and neither
    // must a drilldown series cached against the previous channel or range.
    this.usageStatService.clearSeriesCache();
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
      //
      // The endpoint's own error codes beat the blanket "could not load" this used to show: a
      // hand-typed custom range wider than the API's 366-day guard came back as range_too_large and
      // was rendered as a generic failure, which reads as "the server is broken" rather than "your
      // range is too wide". The generic key stays as the fallback for a body-less failure.
      error: (error: unknown) => {
        this.errorMessage.set(
          error instanceof HttpErrorResponse
            ? apiErrorTranslationKey(error)
            : 'usageStats.errors.loadFailed',
        );
        if (!options.silent) {
          this.isLoading.set(false);
        }
      },
    });
  }
}
