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
  templateUrl: './vote-session-detail-page.html',
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
