import { ScrollingModule } from '@angular/cdk/scrolling';
import {
  Component,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { ForeignEmoteRow } from '../../core/seven-tv/foreign-emote-set.model';
import { LanguageService } from '../../core/i18n/language.service';
import { toLocale } from '../../core/i18n/locale';
import { ListSelection } from '../selection/list-selection';
import { EmoteSprite } from '../emotes/emote-sprite';
import { NoticeBanner } from '../ui/notice-banner';

/** Sprite edge and gutter in px — same numbers as the usage atlas (`ATLAS_CELL_PX`/`ATLAS_GAP_PX` in
 *  `shared/grid/atlas-grid.ts`), kept as local constants rather than imported: that file's row type
 *  and `packAtlasRows` are coupled to `UsageBandKey` and `EmoteUsageTotal`, neither of which this
 *  grid has (spec Falle F4 — this grid is built fresh, not layered onto the atlas). */
const CELL_PX = 64;
const GAP_PX = 4;
/** The name line under each sprite. Picking emotes one by one is a decision about *which* emote,
 *  and the name is what that decision is made on — it is what lands in the target set and what the
 *  collision hint in the confirmation is about. */
const LABEL_PX = 16;
const TILE_PX = CELL_PX + LABEL_PX;
const ROW_PX = TILE_PX + GAP_PX;

/**
 * Which 7TV score field the grid is currently sorted by, or `'none'` for the set's own order.
 * `'none'` is the required default (spec P5'/AK16) — a score must never be the pre-selected sort,
 * since it reads as "popular in this channel" the moment it visually leads, and there is no such
 * thing for a channel this account has no role in.
 */
export type ForeignEmoteSortMode = 'none' | 'topAllTime' | 'trending';

interface SortOption {
  value: ForeignEmoteSortMode;
  labelKey: string;
}

/**
 * The sort options, in menu order. The wording is load-bearing and was changed after the operator
 * read the old labels ("7TV global · Top aller Zeiten") as a statement about the *list* and
 * concluded the grid was showing 7TV's global emotes rather than this channel's set. Two things
 * follow, and both are contracts rather than copy:
 *  - the option names the property of a **single emote** ("wie verbreitet es auf 7TV ist"), never a
 *    source of the list;
 *  - and never plain "Beliebtheit" — a channel-relative popularity does not exist for a channel
 *    nobody here has a role in, and the word would promise one (spec P5').
 */
const SORT_OPTIONS: SortOption[] = [
  { value: 'none', labelKey: 'import.foreignChannel.sort.none' },
  { value: 'topAllTime', labelKey: 'import.foreignChannel.sort.topAllTime' },
  { value: 'trending', labelKey: 'import.foreignChannel.sort.trending' },
];

function columnsForWidth(width: number): number {
  if (!Number.isFinite(width) || width <= 0) {
    return 1;
  }
  return Math.max(1, Math.floor((width + GAP_PX) / (CELL_PX + GAP_PX)));
}

function chunkIntoRows<T>(items: readonly T[], columns: number): T[][] {
  const safeColumns = Math.max(1, columns);
  const rows: T[][] = [];
  for (let i = 0; i < items.length; i += safeColumns) {
    rows.push(items.slice(i, i + safeColumns));
  }
  return rows;
}

/**
 * The selection grid for a foreign channel's 7TV set (spec Falle F4). Deliberately not the
 * `usage-stats-page.html` atlas reused: that one is keyed on our internal `emoteId` Guid and reads
 * `EmoteUsageTotal` for its bands/sparkline/fill-bar, none of which exists for a channel this
 * account has no role in. This grid owns its own `ListSelection<ForeignEmoteRow>` keyed on
 * `sevenTvEmoteId`, the 7TV ObjectID (Regel 8).
 *
 * Virtualized with `CdkVirtualScrollViewport` (HandOfBlood's set is ~956 emotes) — items are
 * chunked into fixed-column rows the same way the atlas is, and for the same reason: a *row*-level
 * `trackBy` (by index) keeps the virtualized row views stable across a resize or a sort change,
 * while the *inner* `@for` tracks each cell by `sevenTvEmoteId` so individual cells reconcile
 * correctly instead of being torn down and rebuilt — a rebuilt cell means a rebuilt
 * `app-emote-sprite`, which starts `visibility: hidden` again until it reloads, i.e. the atlas's
 * known flicker (see `usage-stats-page.ts`'s `trackRow`; this repo has hit that bug once already
 * on a virtualized grid without a stable trackBy).
 *
 * **This viewport is the only scroll container in the dialog.** The pane around it scrolls too by
 * default (`.app-dialog-panel`), and two nested scrollbars over the same list was the reported
 * defect: the viewport is therefore sized against `dvh` with a ceiling, so the dialog's own content
 * stays shorter than the pane and the pane never grows a bar of its own. The ceiling is what keeps
 * a tall desktop screen from turning the sheet into a wall of emotes; the floor keeps a short one
 * usable.
 */
@Component({
  selector: 'app-foreign-emote-grid',
  imports: [EmoteSprite, NoticeBanner, ScrollingModule, TranslocoPipe],
  template: `
    @if (truncated()) {
      <app-notice-banner variant="warning">
        {{
          'import.foreignChannel.truncated'
            | transloco: { loaded: emotes().length, totalCount: totalCount() }
        }}
      </app-notice-banner>
    }

    @if (emotes().length === 0) {
      <p class="text-sm text-fg-muted">{{ 'import.foreignChannel.empty' | transloco }}</p>
    } @else {
      <div class="flex flex-wrap items-center justify-between gap-2">
        <!-- A labelled select, not a segmented control: the old segmented control read as a tab bar
             announcing what the list *was*, which is exactly the misreading this whole rewording
             fixes. "Sortieren nach" in front of it says what the choice does. -->
        <div class="flex items-center gap-2">
          <label class="text-sm text-fg-secondary" [for]="sortSelectId">
            {{ 'import.foreignChannel.sort.label' | transloco }}
          </label>
          <select [id]="sortSelectId" class="app-input-sm" (change)="onSortChange($event)">
            @for (option of sortOptions; track option.value) {
              <option [value]="option.value" [selected]="option.value === sortMode()">
                {{ option.labelKey | transloco }}
              </option>
            }
          </select>
        </div>
        <span class="text-xs text-fg-muted">
          {{
            'import.foreignChannel.selectedCount'
              | transloco: { count: selection.selectedKeys().length }
          }}
        </span>
      </div>

      <!-- The hint belongs to the TILES, not to the sort row: it explains the number printed on
           each of them. So it wraps together with the grid in its own tight column (§7 — spacing is
           the shell's flex gap, and what belongs together more closely than that rhythm wraps
           itself in its own tighter flex column), instead of floating at equal distance between the two
           and reading as a caption for neither. It appears only while a score sort is active; with
           the set order showing there is no number to explain. -->
      <div class="flex flex-col gap-1">
        @if (sortMode() !== 'none') {
          <p class="text-xs text-fg-muted">
            {{ 'import.foreignChannel.sort.scoreHint' | transloco }}
          </p>
        }

        <div
          #gridContainer
          role="group"
          [attr.aria-label]="'import.foreignChannel.grid.ariaLabel' | transloco"
        >
          <cdk-virtual-scroll-viewport
            [itemSize]="rowPx"
            class="h-[clamp(16rem,calc(100dvh-26rem),34rem)]"
          >
            <div
              *cdkVirtualFor="let row of rows(); trackBy: trackRowIndex"
              [style.height.px]="rowPx"
            >
              <div
                class="grid gap-1"
                [style.grid-template-columns]="'repeat(' + columns() + ', ' + cellPx + 'px)'"
              >
                @for (emote of row; track emote.sevenTvEmoteId) {
                  <button
                    type="button"
                    class="flex w-16 flex-col items-stretch"
                    [attr.aria-pressed]="selection.isSelected(emote)"
                    [attr.aria-label]="cellLabel(emote)"
                    [title]="cellLabel(emote)"
                    (click)="onCellClick(emote, $event)"
                    (mousedown)="$event.shiftKey && $event.preventDefault()"
                  >
                    <span
                      class="app-sprite-cell relative block h-16 w-16 transition-shadow hover:inset-ring-1 hover:inset-ring-border-strong"
                    >
                      <app-emote-sprite [url]="emote.imageUrl" [size]="cellPx" />
                      @if (sortMode() !== 'none') {
                        <span
                          class="absolute bottom-0 left-0 px-1 font-mono text-[9px] leading-[1.4] font-medium"
                          [style.background-color]="'var(--ep-sprite-scrim)'"
                          [style.color]="'var(--ep-sprite-scrim-fg)'"
                          >{{ scoreBadge(emote) }}</span
                        >
                      }
                      @if (selection.isSelected(emote)) {
                        <span
                          class="pointer-events-none absolute inset-0 bg-accent-wash/70 inset-ring-2 inset-ring-accent-fg"
                          aria-hidden="true"
                        ></span>
                      }
                    </span>
                    <!-- aria-hidden: the accessible name of the tile already carries the alias and,
                       where it differs, the global default name — announcing the visible line as
                       well would read the alias twice. -->
                    <span
                      [class]="
                        'block truncate text-center text-[10px] leading-4 ' +
                        (selection.isSelected(emote) ? 'font-medium text-fg' : 'text-fg-muted')
                      "
                      aria-hidden="true"
                      >{{ emote.name }}</span
                    >
                  </button>
                }
              </div>
            </div>
          </cdk-virtual-scroll-viewport>
        </div>
      </div>
    }
  `,
  // The grid's own children are the shell's rhythm one level down: without a flex host they would
  // stack as bare blocks with no gap at all, which is what made the score hint cling to whatever
  // happened to sit above it.
  host: { class: 'flex flex-col gap-3' },
})
export class ForeignEmoteGrid {
  readonly emotes = input.required<ForeignEmoteRow[]>();
  /** Whether the source's page cap was hit while 7TV reported more entries (spec F3) — never
   *  silently swallowed, see the notice above. */
  readonly truncated = input(false);
  /** What 7TV reports as the set's total entry count — only meaningful together with `truncated`. */
  readonly totalCount = input<number | null>(null);

  /** The current selection, emitted on every change so a host (the picker step) can gate the
   *  dialog's "weiter" button and build the eventual `ImportRow[]`. */
  readonly selectionChange = output<ForeignEmoteRow[]>();

  private readonly languageService = inject(LanguageService);

  private readonly gridContainerRef = viewChild<ElementRef<HTMLElement>>('gridContainer');
  private readonly containerWidth = signal(0);

  /** Never pre-selected (spec P5'/AK16) — see {@link ForeignEmoteSortMode}. */
  protected readonly sortMode = signal<ForeignEmoteSortMode>('none');

  protected readonly sortOptions = SORT_OPTIONS;
  protected readonly sortSelectId = 'foreign-emote-sort';

  /**
   * Sorting rearranges display order only — it never touches `ListSelection`'s `selectedKeySet`,
   * which is authoritative and key-based (see that class's doc comment), so a selection made before
   * a sort change survives it. `null` scores sort last regardless of direction, since "no 7TV score"
   * is not "the lowest score".
   */
  protected readonly sortedEmotes = computed<ForeignEmoteRow[]>(() => {
    const mode = this.sortMode();
    const items = this.emotes();
    if (mode === 'none') {
      return items;
    }
    return [...items].sort((a, b) => {
      const scoreA = a[mode];
      const scoreB = b[mode];
      if (scoreA === null && scoreB === null) {
        return 0;
      }
      if (scoreA === null) {
        return 1;
      }
      if (scoreB === null) {
        return -1;
      }
      return scoreB - scoreA;
    });
  });

  protected readonly columns = computed(() => columnsForWidth(this.containerWidth()));
  protected readonly rows = computed(() => chunkIntoRows(this.sortedEmotes(), this.columns()));

  protected readonly selection = new ListSelection<ForeignEmoteRow>(
    this.sortedEmotes,
    (row) => row.sevenTvEmoteId,
  );

  protected readonly cellPx = CELL_PX;
  protected readonly rowPx = ROW_PX;

  constructor() {
    // Same pattern as `usage-stats-page.ts`'s sheet-width effect: the column count follows the
    // element that actually holds the cells, not the viewport, and jsdom has no ResizeObserver at
    // all — specs stub the global the same way that page's do.
    effect((onCleanup) => {
      const element = this.gridContainerRef()?.nativeElement;
      if (!element) {
        return;
      }
      this.containerWidth.set(element.clientWidth);
      const observer = new ResizeObserver((entries) => {
        this.containerWidth.set(entries[0].contentRect.width);
      });
      observer.observe(element);
      onCleanup(() => observer.disconnect());
    });
  }

  protected onSortChange(event: Event): void {
    this.sortMode.set((event.target as HTMLSelectElement).value as ForeignEmoteSortMode);
  }

  protected onCellClick(emote: ForeignEmoteRow, event: MouseEvent): void {
    this.selection.onRowClick(emote, event);
    this.selectionChange.emit(this.selection.selectedItems());
  }

  protected trackRowIndex(index: number): number {
    return index;
  }

  /**
   * The tile's accessible name — and its mouse tooltip, since the visible line under the sprite is
   * truncated at 64 px. The alias comes first because that is the name being copied; the global
   * default name follows in brackets only where the two differ, which they do in 296 of
   * HandOfBlood's 956 entries.
   */
  protected cellLabel(emote: ForeignEmoteRow): string {
    return emote.name === emote.defaultName ? emote.name : `${emote.name} (${emote.defaultName})`;
  }

  protected scoreBadge(emote: ForeignEmoteRow): string {
    const value = emote[this.sortMode() as Exclude<ForeignEmoteSortMode, 'none'>];
    if (value === null || value === undefined) {
      return '–';
    }
    const locale = toLocale(this.languageService.lang());
    return value >= 1000
      ? `${(value / 1000).toLocaleString(locale, { maximumFractionDigits: 1 })}k`
      : value.toLocaleString(locale);
  }
}
