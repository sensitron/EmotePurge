import { DatePipe, DecimalPipe, NgOptimizedImage } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ScrollingModule } from '@angular/cdk/scrolling';
import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { Observable } from 'rxjs';

import { AuthService } from '../../core/auth/auth.service';
import { ChannelService } from '../../core/channels/channel.service';
import { VoteSessionResult, VoteSessionResults, VoteType } from '../../core/voting/vote-session.model';
import { VoteSessionService } from '../../core/voting/vote-session.service';
import { EmoteCardHeader } from '../../shared/emotes/emote-card-header';
import { EmoteUsageFilter } from '../../shared/emotes/emote-usage-filter';
import { chunkIntoRows, computeGridColumns } from '../../shared/grid/grid-columns';
import { DeletableEmote, MassDeletePanel } from '../../shared/seven-tv/mass-delete-panel';
import { ListSelection } from '../../shared/selection/list-selection';

// Row height (px) fed to CdkVirtualScrollViewport — see the identical comment in UsageStatsPage.
// Taller than the usage-stats grid: each card also carries a score line and two vote buttons.
const ROW_HEIGHT_PX = 176;

@Component({
  selector: 'app-vote-session-detail-page',
  imports: [ScrollingModule, NgOptimizedImage, DecimalPipe, DatePipe, MassDeletePanel, EmoteCardHeader],
  host: {
    '(window:resize)': 'updateColumns()',
  },
  template: `
    <div class="flex flex-col gap-6">
      @if (results(); as session) {
        <header class="flex flex-wrap items-center justify-between gap-3">
          <div>
            <h2 class="text-lg font-medium">{{ session.title }}</h2>
            <p class="text-sm text-slate-500">
              {{ session.isActive ? 'Aktiv' : 'Beendet' }} · Nutzungsdaten seit
              {{ session.startedAt | date: 'dd.MM.yyyy HH:mm' }}
              @if (session.endedAt) {
                bis {{ session.endedAt | date: 'dd.MM.yyyy HH:mm' }}
              }
            </p>
          </div>
          <button
            type="button"
            class="rounded-md bg-slate-800 px-3 py-1.5 text-sm hover:bg-slate-700"
            (click)="refresh()"
          >
            Aktualisieren
          </button>
        </header>
      }

      <div class="flex flex-wrap items-center gap-2 text-sm">
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
      <p class="text-sm text-slate-400">{{ emotes().length }} von {{ orderedEmotes().length }} Emotes</p>

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

      @if (emotes().length === 0) {
        <p class="text-sm text-slate-400">Keine aktiven Emotes.</p>
      } @else {
        <cdk-virtual-scroll-viewport [itemSize]="rowHeight" class="h-128 rounded-md border border-slate-800">
          <div
            *cdkVirtualFor="let row of rows(); let rowIndex = index"
            class="grid gap-3 px-3 py-2"
            [style.grid-template-columns]="'repeat(' + columns() + ', minmax(0, 1fr))'"
          >
            @for (emote of row; track emote.emoteId; let colIndex = $index) {
              <div
                class="flex h-40 cursor-pointer flex-col gap-1 rounded-md border border-slate-800 bg-slate-900 p-2 text-center transition hover:bg-slate-800/70"
                (click)="selection.onRowClick(emote, rowIndex * columns() + colIndex, $event)"
              >
                <app-emote-card-header [name]="emote.emoteName" [checked]="selection.isSelected(emote)" [showCheckbox]="canManage()" />
                <div class="flex h-10 shrink-0 items-center justify-center">
                  <img [ngSrc]="emote.imageUrl" width="40" height="40" alt="" class="max-h-10 max-w-10 object-contain" />
                </div>
                <span class="text-xs text-slate-500">{{ emote.totalUseCount }}x · {{ emote.score | number: '1.0-1' }}</span>
                <div class="mt-auto flex items-center justify-center gap-1" (click)="$event.stopPropagation()">
                  <button
                    type="button"
                    class="flex items-center gap-1 rounded-md p-1 transition hover:bg-slate-800"
                    [class]="emote.myVote === voteType.Keep ? 'text-emerald-400' : 'text-slate-500'"
                    (click)="vote(emote, voteType.Keep)"
                    aria-label="Behalten"
                    [attr.title]="(emote.myVote === voteType.Keep ? 'Vote zurücknehmen' : 'Behalten') + ' (' + emote.keepVotes + ')'"
                  >
                    <svg viewBox="0 0 24 24" width="16" height="16" fill="currentColor">
                      <path
                        d="M2 21h2a1 1 0 0 0 1-1v-9a1 1 0 0 0-1-1H2v11ZM22 11.5a2 2 0 0 0-2-2h-5.6l.8-4a2 2 0 0 0-2-2.4h-.2L8 9v12h11a2 2 0 0 0 1.9-1.4l1.9-6.6a2 2 0 0 0 .1-.5v-1Z"
                      />
                    </svg>
                    <span class="text-xs">{{ emote.keepVotes }}</span>
                  </button>
                  <button
                    type="button"
                    class="flex items-center gap-1 rounded-md p-1 transition hover:bg-slate-800"
                    [class]="emote.myVote === voteType.Delete ? 'text-red-400' : 'text-slate-500'"
                    (click)="vote(emote, voteType.Delete)"
                    aria-label="Löschen vorschlagen"
                    [attr.title]="(emote.myVote === voteType.Delete ? 'Vote zurücknehmen' : 'Löschen vorschlagen') + ' (' + emote.deleteVotes + ')'"
                  >
                    <svg viewBox="0 0 24 24" width="16" height="16" fill="currentColor" style="transform: rotate(180deg)">
                      <path
                        d="M2 21h2a1 1 0 0 0 1-1v-9a1 1 0 0 0-1-1H2v11ZM22 11.5a2 2 0 0 0-2-2h-5.6l.8-4a2 2 0 0 0-2-2.4h-.2L8 9v12h11a2 2 0 0 0 1.9-1.4l1.9-6.6a2 2 0 0 0 .1-.5v-1Z"
                      />
                    </svg>
                    <span class="text-xs">{{ emote.deleteVotes }}</span>
                  </button>
                </div>
              </div>
            }
          </div>
        </cdk-virtual-scroll-viewport>
      }
    </div>
  `,
})
export class VoteSessionDetailPage {
  readonly channelName = input.required<string>();
  readonly sessionId = input.required<string>();

  private readonly voteSessionService = inject(VoteSessionService);
  private readonly channelService = inject(ChannelService);
  private readonly authService = inject(AuthService);

  protected readonly voteType = VoteType;
  protected readonly currentUser = this.authService.currentUser;

  protected readonly rowHeight = ROW_HEIGHT_PX;
  protected readonly columns = signal(computeGridColumns(window.innerWidth));

  protected readonly results = signal<VoteSessionResults | null>(null);
  protected readonly canManage = signal(false);
  protected readonly activeEmoteSetId = signal<string | null>(null);
  protected readonly errorMessage = signal<string | null>(null);

  // Freezes the card order (by emote id) across post-vote reloads, since the backend sorts by
  // score descending — without this, voting an emote's score down to the bottom instantly yanks
  // its card to the end of the list while the user is still looking at it.
  protected readonly orderedEmoteIds = signal<string[] | null>(null);

  protected readonly orderedEmotes = computed(() => {
    const results = this.results();
    if (!results) {
      return [];
    }
    const order = this.orderedEmoteIds();
    if (!order) {
      return results.emotes;
    }

    const byId = new Map(results.emotes.map((emote) => [emote.emoteId, emote]));
    const ordered: VoteSessionResult[] = [];
    for (const id of order) {
      const emote = byId.get(id);
      if (emote) {
        ordered.push(emote);
        byId.delete(id);
      }
    }
    // Emotes not present in the frozen order (e.g. synced since the last freeze) are appended at
    // the end, in the backend's current order among themselves.
    for (const emote of results.emotes) {
      if (byId.has(emote.emoteId)) {
        ordered.push(emote);
      }
    }
    return ordered;
  });

  protected readonly usageFilter = new EmoteUsageFilter<VoteSessionResult>(() => this.selection.clear());

  protected readonly emotes = computed(() => this.usageFilter.apply(this.orderedEmotes()));

  protected readonly rows = computed(() => chunkIntoRows(this.emotes(), this.columns()));
  protected readonly selection = new ListSelection(this.emotes);

  protected readonly selectedForDelete = computed<DeletableEmote[]>(() =>
    this.selection.selected().map((emote) => ({
      emoteId: emote.emoteId,
      sevenTvEmoteId: emote.sevenTvEmoteId,
      name: emote.emoteName,
    })),
  );

  constructor() {
    // Deferred, not called directly — see the identical comment in VoteSessionListPage.
    effect(() => this.load());
  }

  protected updateColumns(): void {
    this.columns.set(computeGridColumns(window.innerWidth));
  }

  private load(options: { freeze: boolean } = { freeze: true }): void {
    const channelName = this.channelName();
    const sessionId = Number(this.sessionId());

    // voteSessionAccessGuard already verified login + audience eligibility before this component
    // was even mounted — this call should always succeed for whoever reached this page normally.
    this.voteSessionService.getResults(channelName, sessionId).subscribe({
      next: (results) => {
        this.results.set(results);
        if (options.freeze) {
          this.orderedEmoteIds.set(results.emotes.map((emote) => emote.emoteId));
        }
      },
      error: () => this.errorMessage.set('Abstimmung konnte nicht geladen werden.'),
    });

    this.channelService.getStatus(channelName).subscribe({
      next: (status) => {
        this.canManage.set(true);
        this.activeEmoteSetId.set(status.activeEmoteSetId);
      },
      error: () => this.canManage.set(false),
    });
  }

  protected refresh(): void {
    this.load();
  }

  protected vote(emote: VoteSessionResult, type: VoteType): void {
    // Defensive fallback, not the primary gate — voteSessionAccessGuard already required a login
    // to reach this page, but the session cookie/token can still expire while already viewing it.
    if (!this.currentUser()) {
      this.authService.login(window.location.pathname);
      return;
    }

    this.errorMessage.set(null);

    // Clicking the same vote type again retracts it, returning the emote to the neutral state —
    // otherwise a Keep vote could only ever be overwritten by a Delete vote, never undone.
    const request$: Observable<unknown> =
      emote.myVote === type
        ? this.voteSessionService.retractVote(this.channelName(), Number(this.sessionId()), emote.emoteId)
        : this.voteSessionService.castVote(this.channelName(), Number(this.sessionId()), emote.emoteId, type);

    request$.subscribe({
      next: () => this.load({ freeze: false }),
      error: (error: HttpErrorResponse) => this.handleVoteError(error),
    });
  }

  // Status codes are unambiguous for this one endpoint (VoteEligibilityFilter/CastVoteAsync each
  // return a distinct code per reason — see Program.cs), so a plain status switch is enough without
  // needing to parse the response body.
  private handleVoteError(error: HttpErrorResponse): void {
    if (error.status === 401) {
      this.authService.handleSessionExpired();
      return;
    }

    switch (error.status) {
      case 403:
        this.errorMessage.set('Du darfst in dieser Abstimmung nicht mitvoten (falsche Rolle für diese Session).');
        break;
      case 409:
        this.errorMessage.set('Diese Abstimmung ist bereits beendet.');
        break;
      case 404:
        this.errorMessage.set('Abstimmung oder Channel nicht gefunden.');
        break;
      case 400:
        this.errorMessage.set('Dieses Emote ist nicht mehr abstimmbar (unbekannt oder bereits archiviert).');
        break;
      default:
        this.errorMessage.set('Vote konnte nicht gespeichert werden.');
    }
  }

  protected onDeleted(deletedIds: string[]): void {
    this.results.update((results) =>
      results ? { ...results, emotes: results.emotes.filter((emote) => !deletedIds.includes(emote.emoteId)) } : results,
    );
    this.selection.clear();
  }
}
