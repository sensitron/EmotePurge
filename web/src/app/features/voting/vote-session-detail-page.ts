import { DecimalPipe, NgOptimizedImage } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ScrollingModule } from '@angular/cdk/scrolling';
import { Component, computed, effect, inject, input, signal } from '@angular/core';

import { AuthService } from '../../core/auth/auth.service';
import { ChannelService } from '../../core/channels/channel.service';
import { VoteSessionResult, VoteSessionResults, VoteType } from '../../core/voting/vote-session.model';
import { VoteSessionService } from '../../core/voting/vote-session.service';
import { chunkIntoRows, computeGridColumns } from '../../shared/grid/grid-columns';
import { DeletableEmote, MassDeletePanel } from '../../shared/seven-tv/mass-delete-panel';
import { ListSelection } from '../../shared/selection/list-selection';

// Row height (px) fed to CdkVirtualScrollViewport — see the identical comment in UsageStatsPage.
// Taller than the usage-stats grid: each card also carries a score line and two vote buttons.
const ROW_HEIGHT_PX = 176;

@Component({
  selector: 'app-vote-session-detail-page',
  imports: [ScrollingModule, NgOptimizedImage, DecimalPipe, MassDeletePanel],
  host: {
    '(window:resize)': 'updateColumns()',
  },
  template: `
    <div class="flex flex-col gap-6">
      @if (results(); as session) {
        <header>
          <h2 class="text-lg font-medium">{{ session.title }}</h2>
          <p class="text-sm text-slate-500">{{ session.isActive ? 'Aktiv' : 'Beendet' }}</p>
        </header>
      }

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
              <div class="flex h-40 flex-col items-center justify-center gap-1 rounded-md border border-slate-800 bg-slate-900 p-2 text-center">
                @if (canManage()) {
                  <input
                    type="checkbox"
                    class="self-start"
                    [checked]="selection.isSelected(emote)"
                    (click)="selection.onRowClick(emote, rowIndex * columns() + colIndex, $event)"
                  />
                }
                <img [ngSrc]="emote.imageUrl" width="40" height="40" alt="" />
                <span class="w-full truncate text-xs">{{ emote.emoteName }}</span>
                <span class="text-xs text-slate-500">{{ emote.totalUseCount }}x · {{ emote.score | number: '1.0-1' }}</span>
                <div class="flex items-center gap-1">
                  <button
                    type="button"
                    class="flex items-center gap-1 rounded-md p-1 transition hover:bg-slate-800"
                    [class]="emote.myVote === voteType.Keep ? 'text-emerald-400' : 'text-slate-500'"
                    (click)="vote(emote, voteType.Keep)"
                    aria-label="Behalten"
                    [attr.title]="'Behalten (' + emote.keepVotes + ')'"
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
                    [attr.title]="'Löschen vorschlagen (' + emote.deleteVotes + ')'"
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

  protected readonly emotes = computed(() => this.results()?.emotes ?? []);
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

  private load(): void {
    const channelName = this.channelName();
    const sessionId = Number(this.sessionId());

    // Deliberately no auth requirement here — this must render for an anonymous share-link
    // visitor too; MyVote just stays null for them until they log in and vote.
    this.voteSessionService.getResults(channelName, sessionId).subscribe({
      next: (results) => this.results.set(results),
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

  protected vote(emote: VoteSessionResult, type: VoteType): void {
    if (!this.currentUser()) {
      this.authService.login(window.location.pathname);
      return;
    }

    this.errorMessage.set(null);
    this.voteSessionService.castVote(this.channelName(), Number(this.sessionId()), emote.emoteId, type).subscribe({
      next: () => this.load(),
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
