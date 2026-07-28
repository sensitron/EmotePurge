import { NgOptimizedImage } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ScrollingModule } from '@angular/cdk/scrolling';
import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { AuthService } from '../../core/auth/auth.service';
import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { pluralKey } from '../../core/i18n/plural';
import { EmoteUsageTotal } from '../../core/usage-stats/usage-stat.model';
import { UsageStatService } from '../../core/usage-stats/usage-stat.service';
import { EmoteCardHeader } from '../../shared/emotes/emote-card-header';
import { EmoteUsageFilter } from '../../shared/emotes/emote-usage-filter';
import { chunkIntoRows, computeGridColumns } from '../../shared/grid/grid-columns';
import { DeletableEmote, MassDeletePanel } from '../../shared/seven-tv/mass-delete-panel';
import { ListSelection } from '../../shared/selection/list-selection';

type SortDirection = 'asc' | 'desc';

// Row height (px) fed to CdkVirtualScrollViewport — must match the fixed card height + row
// wrapper padding below, since CDK's fixed-size strategy assumes every virtualized row is the same height.
const ROW_HEIGHT_PX = 112;

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
  imports: [ScrollingModule, NgOptimizedImage, MassDeletePanel, EmoteCardHeader, TranslocoPipe],
  host: {
    '(window:resize)': 'updateColumns()',
  },
  templateUrl: './usage-stats-page.html',
})
export class UsageStatsPage {
  readonly channelName = input.required<string>();

  private readonly usageStatService = inject(UsageStatService);
  private readonly emoteAdminService = inject(EmoteAdminService);
  private readonly authService = inject(AuthService);

  protected readonly rowHeight = ROW_HEIGHT_PX;
  protected readonly columns = signal(computeGridColumns(window.innerWidth));

  protected readonly from = signal(toIsoDate(daysAgo(7)));
  protected readonly to = signal(toIsoDate(new Date()));
  protected readonly sortDirection = signal<SortDirection>('desc');

  protected readonly emotes = signal<EmoteUsageTotal[]>([]);
  protected readonly activeEmoteSetId = signal<string | null>(null);
  protected readonly isLoading = signal(false);
  protected readonly errorMessage = signal<string | null>(null);

  protected readonly usageFilter = new EmoteUsageFilter<EmoteUsageTotal>(() => this.selection.clear());

  protected readonly filteredEmotes = computed(() => this.usageFilter.apply(this.emotes()));

  protected readonly sortedEmotes = computed(() => {
    const items = [...this.filteredEmotes()];
    items.sort((a, b) => (this.sortDirection() === 'desc' ? b.totalUseCount - a.totalUseCount : a.totalUseCount - b.totalUseCount));
    return items;
  });

  protected readonly rows = computed(() => chunkIntoRows(this.sortedEmotes(), this.columns()));

  protected readonly emoteCountKey = computed(() => pluralKey(this.emotes().length, 'emoteCount'));

  protected readonly selection = new ListSelection(this.sortedEmotes);

  protected readonly selectedForDelete = computed<DeletableEmote[]>(() =>
    this.selection.selected().map((emote) => ({
      emoteId: emote.emoteId,
      sevenTvEmoteId: emote.sevenTvEmoteId,
      name: emote.emoteName,
    })),
  );

  constructor() {
    effect(() => {
      this.load(this.channelName(), this.from(), this.to());
    });
  }

  protected updateColumns(): void {
    this.columns.set(computeGridColumns(window.innerWidth));
  }

  protected refresh(): void {
    this.load(this.channelName(), this.from(), this.to());
  }

  protected setPreset(days: number): void {
    this.from.set(toIsoDate(daysAgo(days)));
    this.to.set(toIsoDate(new Date()));
  }

  protected toggleSort(): void {
    this.sortDirection.update((direction) => (direction === 'desc' ? 'asc' : 'desc'));
  }

  protected onDeleted(deletedIds: string[]): void {
    this.emotes.update((items) => items.filter((item) => !deletedIds.includes(item.emoteId)));
    this.selection.clear();
  }

  private load(channelName: string, from: string, to: string): void {
    this.isLoading.set(true);
    this.errorMessage.set(null);

    this.emoteAdminService.getActiveEmoteSetId(channelName).subscribe({
      next: (result) => this.activeEmoteSetId.set(result.activeEmoteSetId),
      error: () => this.activeEmoteSetId.set(null),
    });

    this.usageStatService.getTotals(channelName, from, to).subscribe({
      next: (emotes) => {
        this.emotes.set(emotes);
        this.selection.clear();
        this.isLoading.set(false);
      },
      error: (error: HttpErrorResponse) => {
        if (error.status === 401) {
          this.authService.handleSessionExpired();
          return;
        }
        this.errorMessage.set('usageStats.errors.loadFailed');
        this.isLoading.set(false);
      },
    });
  }
}
