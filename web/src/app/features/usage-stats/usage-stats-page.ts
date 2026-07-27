import { NgOptimizedImage } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ScrollingModule } from '@angular/cdk/scrolling';
import { Component, computed, effect, inject, input, signal } from '@angular/core';

import { AuthService } from '../../core/auth/auth.service';
import { ChannelService } from '../../core/channels/channel.service';
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
  imports: [ScrollingModule, NgOptimizedImage, MassDeletePanel, EmoteCardHeader],
  host: {
    '(window:resize)': 'updateColumns()',
  },
  template: `
    <div class="flex flex-col gap-6">
      <header class="flex flex-wrap items-center justify-between gap-4">
        <h2 class="text-lg font-medium">Emote-Nutzung</h2>
        <div class="flex flex-wrap items-center gap-2 text-sm">
          <button type="button" class="rounded-md bg-slate-800 px-3 py-1.5 hover:bg-slate-700" (click)="setPreset(0)">
            Heute
          </button>
          <button type="button" class="rounded-md bg-slate-800 px-3 py-1.5 hover:bg-slate-700" (click)="setPreset(7)">
            7 Tage
          </button>
          <button type="button" class="rounded-md bg-slate-800 px-3 py-1.5 hover:bg-slate-700" (click)="setPreset(30)">
            30 Tage
          </button>
          <input
            type="date"
            [value]="from()"
            (change)="from.set($any($event.target).value)"
            class="rounded-md border border-slate-700 bg-slate-950 px-2 py-1.5"
          />
          <span class="text-slate-500">bis</span>
          <input
            type="date"
            [value]="to()"
            (change)="to.set($any($event.target).value)"
            class="rounded-md border border-slate-700 bg-slate-950 px-2 py-1.5"
          />
          <button type="button" class="rounded-md bg-slate-800 px-3 py-1.5 hover:bg-slate-700" (click)="toggleSort()">
            Nutzung ({{ sortDirection() === 'desc' ? '↓' : '↑' }})
          </button>
          <input
            type="number"
            min="0"
            placeholder="Min"
            [value]="usageFilter.min() ?? ''"
            (change)="usageFilter.setMinCount($any($event.target).value)"
            class="w-20 rounded-md border border-slate-700 bg-slate-950 px-2 py-1.5"
          />
          <input
            type="number"
            min="0"
            placeholder="Max"
            [value]="usageFilter.max() ?? ''"
            (change)="usageFilter.setMaxCount($any($event.target).value)"
            class="w-20 rounded-md border border-slate-700 bg-slate-950 px-2 py-1.5"
          />
          <input
            type="text"
            placeholder="Name (z.B. *cat*, ?og)"
            [value]="usageFilter.nameQuery()"
            (input)="usageFilter.setNameFilter($any($event.target).value)"
            class="w-40 rounded-md border border-slate-700 bg-slate-950 px-2 py-1.5"
          />
          <button
            type="button"
            class="rounded-md px-3 py-1.5"
            [class]="usageFilter.isUnusedActive() ? 'bg-purple-600 hover:bg-purple-500' : 'bg-slate-800 hover:bg-slate-700'"
            (click)="usageFilter.toggleUnused()"
          >
            {{ usageFilter.isUnusedActive() ? 'Alle anzeigen' : 'Nur ungenutzte (0x)' }}
          </button>
        </div>
      </header>

      <p class="text-sm text-slate-400">{{ sortedEmotes().length }} von {{ emotes().length }} Emotes</p>

      @if (activeEmoteSetId(); as setId) {
        <app-mass-delete-panel
          [setId]="setId"
          [channelName]="channelName()"
          [selectedEmotes]="selectedForDelete()"
          (deleted)="onDeleted($event)"
        />
      }

      @if (errorMessage(); as message) {
        <p class="rounded-md bg-red-950 px-4 py-3 text-sm text-red-300">{{ message }}</p>
      }

      @if (isLoading()) {
        <p class="text-sm text-slate-400">Lädt…</p>
      } @else if (sortedEmotes().length === 0) {
        <p class="text-sm text-slate-400">Keine aktiven Emotes für diesen Zeitraum.</p>
      } @else {
        <cdk-virtual-scroll-viewport [itemSize]="rowHeight" class="h-128 rounded-md border border-slate-800">
          <div
            *cdkVirtualFor="let row of rows(); let rowIndex = index"
            class="grid gap-3 px-3 py-2"
            [style.grid-template-columns]="'repeat(' + columns() + ', minmax(0, 1fr))'"
          >
            @for (emote of row; track emote.emoteId; let colIndex = $index) {
              <div
                class="flex h-24 cursor-pointer flex-col gap-1 rounded-md border border-slate-800 bg-slate-900 p-2 text-center transition hover:bg-slate-800/70"
                (click)="selection.onRowClick(emote, rowIndex * columns() + colIndex, $event)"
              >
                <app-emote-card-header [name]="emote.emoteName" [checked]="selection.isSelected(emote)" />
                <div class="flex h-10 shrink-0 items-center justify-center">
                  <img [ngSrc]="emote.imageUrl" width="40" height="40" alt="" class="max-h-10 max-w-10 object-contain" />
                </div>
                <span class="text-xs text-slate-400">{{ emote.totalUseCount }}x</span>
              </div>
            }
          </div>
        </cdk-virtual-scroll-viewport>
      }
    </div>
  `,
})
export class UsageStatsPage {
  readonly channelName = input.required<string>();

  private readonly usageStatService = inject(UsageStatService);
  private readonly channelService = inject(ChannelService);
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

    this.channelService.getStatus(channelName).subscribe({
      next: (status) => this.activeEmoteSetId.set(status.activeEmoteSetId),
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
        this.errorMessage.set('Nutzungsdaten konnten nicht geladen werden.');
        this.isLoading.set(false);
      },
    });
  }
}
